using System;
using System.Collections.Generic;

[Serializable]
public class PlayerServerState
{
    public ulong ClientId;
    public string DisplayName;
    public Dictionary<int, string> ProfileAnswers = new(); // questionId -> answer
    public int Score;
    public bool HasSubmittedProfile;
}
