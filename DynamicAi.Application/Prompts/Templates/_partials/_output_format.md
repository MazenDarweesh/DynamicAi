You MUST respond with a single JSON object. ```json``` fences optional.

JSON SCHEMA:
{
  "intent": "DataRetrieval | Analysis | Chat | Prediction | PredictionWithDataRetrieval",
  "content": {
    "sql": "SELECT ... (only for DataRetrieval/Analysis/PredictionWithDataRetrieval, empty string for Chat/Prediction)",
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
- For Prediction: fill "message" with the prediction analysis (use ONLY if data is already visible/contextual). Leave "sql" empty.
- For PredictionWithDataRetrieval: fill "sql" to fetch the necessary data FIRST. Leave "message" empty.
- ALWAYS include the "intent" field so the system knows how to process your response.
- "confidence" reflects how confident you are in the result (0.0 = very uncertain, 1.0 = certain).
