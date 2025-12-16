using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(menuName = "GetToKnowMe/QuestionSet")]
public class QuestionSet : ScriptableObject
{
    public List<QuestionDefinition> questions;
}

[System.Serializable]
public class QuestionDefinition
{
    public int id;
    public string text;
}
