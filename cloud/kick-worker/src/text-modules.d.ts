// Wrangler's default rules import *.txt as text (used for the generated browser script).
declare module "*.txt" {
  const text: string;
  export default text;
}
