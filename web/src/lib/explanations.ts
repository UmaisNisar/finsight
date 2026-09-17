/*
  One-sentence explanations for the few badges whose names are jargon. Shown as help tags; each supplements a visible
  label and is never the only place something essential is said.
*/

export const AI_CATEGORY_EXPLANATION = 'Gemini chose this category because FinSight’s rules didn’t recognize the merchant.';

export const CAVEAT_EXPLANATION: Record<string, string> = {
  'Possible pay stub': 'This looks like a pay stub or other income document rather than a bank statement, so it isn’t selected.',
  'Weak match': 'This email only partly looks like a statement, so it isn’t selected. Check it before importing.',
};
