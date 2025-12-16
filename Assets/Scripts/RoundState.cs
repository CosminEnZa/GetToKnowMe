using System;

[Serializable]
public class RoundState
{
    public RoundType RoundType;
    public ulong TargetClientId;
    public ulong GuesserClientId; // For Type 2, filled when 1st answer arrives
    public int QuestionId;

    public string RegisteredGuessText;
    public bool HasGuess => GuesserClientId != 0;
    public bool IsResolved;
}