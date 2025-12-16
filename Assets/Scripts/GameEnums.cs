public enum GamePhase
{
    None,
    Lobby,
    ProfileSetup,
    RoundInProgress,
    ReviewingAnswer,
    Results
}

public enum RoundType
{
    OneGuessesAnother, // Type 1: one player guesses about another
    EveryoneGuessesOne // Type 2: everyone guesses about the same target, first answer counts
}
