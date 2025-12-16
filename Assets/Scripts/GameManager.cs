
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[System.Serializable]
public struct SimplePlayerData : INetworkSerializable
{
    public ulong ClientId;
    public string DisplayName;
    public int Score;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref DisplayName);
        serializer.SerializeValue(ref Score);
    }
}
public class GameManager : NetworkBehaviour
{
    //this
    public static GameManager Instance { get; private set; }

    [SerializeField] private QuestionSet questionSet;

    private readonly Dictionary<ulong, PlayerServerState> _players = new();
    private readonly List<int> _usedQuestionIds = new();

    private GamePhase _phase = GamePhase.None;
    private RoundState _currentRound;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        _players[clientId] = new PlayerServerState
        {
            ClientId = clientId,
            DisplayName = $"Player {clientId}",
            Score = 0
        };



    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;
        _players.Remove(clientId);
        SendPlayerListToClients();
    }

    private SimplePlayerData[] GetSimplePlayerList()
    {
        var list = new List<SimplePlayerData>();
        foreach (var kvp in _players)
        {
            list.Add(new SimplePlayerData
            {
                ClientId = kvp.Key,
                DisplayName = kvp.Value.DisplayName,
                Score = kvp.Value.Score
            });
        }
        return list.ToArray();
    }

    private void SendPlayerListToClients()
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;

        foreach (var kvp in _players)
        {
            var p = kvp.Value;

            if (!first) sb.Append(';');
            first = false;

            sb.Append(p.ClientId);
            sb.Append('|');

            var safeName = (p.DisplayName ?? "").Replace("|", "/").Replace(";", ",");
            sb.Append(safeName);
            sb.Append('|');

            sb.Append(p.Score);
        }

        
        SendPlayerListClientRpc(sb.ToString());

    }

    [ClientRpc]
    private void SendPlayerListClientRpc(string packedPlayers)
    {
        // packedPlayers format: "clientId|name|score;clientId|name|score;..."
        var list = new List<SimplePlayerData>();

        if (!string.IsNullOrEmpty(packedPlayers))
        {
            var entries = packedPlayers.Split(';');
            foreach (var entry in entries)
            {
                var parts = entry.Split('|');
                if (parts.Length != 3) continue;

                if (!ulong.TryParse(parts[0], out var clientId)) continue;
                var name = parts[1];
                if (!int.TryParse(parts[2], out var score)) continue;

                list.Add(new SimplePlayerData
                {
                    ClientId = clientId,
                    DisplayName = name,
                    Score = score
                });
            }
        }

        // TODO: send this list to your UI
        // LobbyUI.Instance.UpdatePlayerList(list);
    }

    // Called by host only when pressing "Start Game"
    public void Host_StartGame()
    {
        if (!IsServer) return;

        _phase = GamePhase.ProfileSetup;
        _usedQuestionIds.Clear();

        // Build "id|text;id|text;..."
        var sb = new System.Text.StringBuilder();
        bool first = true;

        foreach (var q in questionSet.questions)
        {
            if (!first) sb.Append(';');
            first = false;

            sb.Append(q.id);
            sb.Append('|');

            var safeText = q.text.Replace("|", "/").Replace(";", ",");
            sb.Append(safeText);
        }

        StartProfileSetupClientRpc(sb.ToString());
    }

    private string[] GetQuestionTexts()
    {
        var list = new List<string>();
        foreach (var q in questionSet.questions)
            list.Add(q.text);
        return list.ToArray();
    }

    [ClientRpc]
    private void StartProfileSetupClientRpc(string packedQuestions)
    {
        // packedQuestions: "id|text;id|text;..."

        var questions = new List<QuestionDefinitionRuntime>();

        if (!string.IsNullOrEmpty(packedQuestions))
        {
            var entries = packedQuestions.Split(';');
            foreach (var entry in entries)
            {
                var parts = entry.Split('|');
                if (parts.Length != 2) continue;

                if (!int.TryParse(parts[0], out var id)) continue;
                var text = parts[1];

                questions.Add(new QuestionDefinitionRuntime
                {
                    id = id,
                    text = text
                });
            }
        }

        // TODO: send to your UI:
        // ProfileSetupUI.Instance.ShowQuestions(questions);
    }

    public struct QuestionDefinitionRuntime
    {
        public int id;
        public string text;
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitProfileServerRpc(
    string displayName,
    int[] questionIds,
    StringArraySerializable answers,
    ServerRpcParams rpcParams = default)
    {
        var clientId = rpcParams.Receive.SenderClientId;

        if (!_players.TryGetValue(clientId, out var player))
            return;

        player.DisplayName = displayName;
        player.ProfileAnswers.Clear();

        var answerArray = answers.Items ?? System.Array.Empty<string>();
        int count = Mathf.Min(questionIds.Length, answerArray.Length);

        for (int i = 0; i < count; i++)
            player.ProfileAnswers[questionIds[i]] = answerArray[i];

        player.HasSubmittedProfile = true;

        CheckAllProfilesSubmitted();
    }

    private void CheckAllProfilesSubmitted()
    {
        foreach (var p in _players.Values)
        {
            if (!p.HasSubmittedProfile) return;
        }

        // Everyone done, start first round
        StartNextRound();
    }

    private void StartNextRound()
    {
        _phase = GamePhase.RoundInProgress;

        // Choose round type
        _currentRound = new RoundState();
        _currentRound.RoundType = (Random.value < 0.5f)
            ? RoundType.OneGuessesAnother
            : RoundType.EveryoneGuessesOne;

        // Choose target
        var playerIds = new List<ulong>(_players.Keys);
        _currentRound.TargetClientId = playerIds[Random.Range(0, playerIds.Count)];

        // Choose guesser (for Type1 only)
        if (_currentRound.RoundType == RoundType.OneGuessesAnother)
        {
            ulong guesser;
            do
            {
                guesser = playerIds[Random.Range(0, playerIds.Count)];
            } while (guesser == _currentRound.TargetClientId);

            _currentRound.GuesserClientId = guesser;
        }

        // Choose question for this target (avoid repeats until pool exhausted)
        var available = new List<QuestionDefinition>(questionSet.questions);
        available.RemoveAll(q => _usedQuestionIds.Contains(q.id));
        if (available.Count == 0)
        {
            _usedQuestionIds.Clear();
            available = new List<QuestionDefinition>(questionSet.questions);
        }

        var chosenQuestion = available[Random.Range(0, available.Count)];
        _usedQuestionIds.Add(chosenQuestion.id);
        _currentRound.QuestionId = chosenQuestion.id;

        // Send UI instruction to all players
        StartRoundClientRpc(
            _currentRound.RoundType,
            _currentRound.TargetClientId,
            _currentRound.GuesserClientId,
            _currentRound.QuestionId,
            chosenQuestion.text,
            roundTimeSeconds: 20f);
    }

    [ClientRpc]
    private void StartRoundClientRpc(
        RoundType roundType,
        ulong targetClientId,
        ulong guesserClientId,
        int questionId,
        string questionText,
        float roundTimeSeconds)
    {
        // Client-side UI:
        // - If I'm target: show "waiting for answer…"
        // - If I'm guesser in Type1: show input field
        // - If Type2: everyone except target shows input field
        // After user submits, call SubmitGuessServerRpc.
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitGuessServerRpc(string guessText, ServerRpcParams rpcParams = default)
    {
        if (_phase != GamePhase.RoundInProgress) return;

        var senderId = rpcParams.Receive.SenderClientId;

        // Target cannot guess
        if (senderId == _currentRound.TargetClientId) return;

        if (_currentRound.RoundType == RoundType.OneGuessesAnother)
        {
            // Only designated guesser can answer
            if (senderId != _currentRound.GuesserClientId) return;

            // Only accept first guess for safety
            if (_currentRound.HasGuess) return;

            _currentRound.RegisteredGuessText = guessText;
            _currentRound.GuesserClientId = senderId;
        }
        else // EveryoneGuessesOne: first answer wins, no pre-checking
        {
            if (_currentRound.HasGuess) return; // somebody already buzzed in

            _currentRound.RegisteredGuessText = guessText;
            _currentRound.GuesserClientId = senderId;
        }

        // Once a guess is registered, move to review phase
        BeginReviewPhase();
    }

    private void BeginReviewPhase()
    {
        _phase = GamePhase.ReviewingAnswer;

        if (!_players.TryGetValue(_currentRound.TargetClientId, out var targetPlayer))
            return;

        string originalAnswer = "";
        if (targetPlayer.ProfileAnswers.TryGetValue(_currentRound.QuestionId, out var stored))
            originalAnswer = stored;

        // Show question + original answer + guess to everyone
        AskForJudgementClientRpc(
            _currentRound.TargetClientId,
            _currentRound.GuesserClientId,
            _currentRound.QuestionId,
            GetQuestionTextById(_currentRound.QuestionId),
            originalAnswer,
            _currentRound.RegisteredGuessText);
    }

    private string GetQuestionTextById(int id)
    {
        foreach (var q in questionSet.questions)
            if (q.id == id) return q.text;
        return "Unknown question";
    }

    [ClientRpc]
    private void AskForJudgementClientRpc(
        ulong targetClientId,
        ulong guesserClientId,
        int questionId,
        string questionText,
        string originalAnswer,
        string guessText)
    {
        // All players:
        //  - Show questionText, originalAnswer, guessText
        // Only targetClientId:
        //  - Enable "Correct" / "Wrong" buttons that call SubmitJudgementServerRpc
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitJudgementServerRpc(bool isCorrect, ServerRpcParams rpcParams = default)
    {
        var senderId = rpcParams.Receive.SenderClientId;

        // Only target is allowed to judge
        if (senderId != _currentRound.TargetClientId) return;
        if (_currentRound.IsResolved) return;

        _currentRound.IsResolved = true;

        if (isCorrect)
        {
            if (_players.TryGetValue(_currentRound.GuesserClientId, out var guesser))
                guesser.Score += 2;

            if (_players.TryGetValue(_currentRound.TargetClientId, out var target))
                target.Score += 1;
        }

        // Inform everyone of result + new scores
        RoundResultClientRpc(
            isCorrect,
            GetSimplePlayerList());

        // Optionally wait a bit, then StartNextRound()
    }

    [ClientRpc]
    private void RoundResultClientRpc(bool isCorrect, SimplePlayerData[] playerScores)
    {
        // Show round result UI (Correct/Wrong, who gained points, updated leaderboard)
        // After a short delay, host will start next round.
    }
}
public struct StringArraySerializable : INetworkSerializable
{
    public string[] Items;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        int length = Items?.Length ?? 0;
        serializer.SerializeValue(ref length);

        if (serializer.IsReader)
            Items = new string[length];

        for (int i = 0; i < length; i++)
            serializer.SerializeValue(ref Items[i]);
    }
}