You MUST respond with a single JSON object. ```json``` fences optional.

JSON SCHEMA:
{
  "intent": "DataRetrieval | Analysis | Chat | Prediction",
  "content": {
    "sql": "SELECT ... (only for DataRetrieval/Analysis, empty string for Chat/Prediction)",
    "message": "Response text (only for Chat/Prediction, empty string for SQL)",
    "explanation": "Brief explanation of your reasoning"
  },
  "confidence": 0.0-1.0,
  "needs_clarification": false,
  "clarification_question": "Arabic question or null"
}

RULES:
- For DataRetrieval/Analysis: fill "sql" and "explanation". Leave "message" empty.
- For Chat: fill "message". Leave "sql" empty.
- For Prediction: fill "message" with the prediction analysis. Leave "sql" empty.
- ALWAYS include the "intent" field so the system knows how to process your response.
- "confidence" reflects how confident you are in the result (0.0 = very uncertain, 1.0 = certain).
