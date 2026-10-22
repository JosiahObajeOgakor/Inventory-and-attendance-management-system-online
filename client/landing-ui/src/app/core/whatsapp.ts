// The shop's WhatsApp Business number (international format, digits only): the number connected to the sales assistant in Twilio.
// The server says which number that is (GET /api/webchat/status, read from the same Twilio setting the chat uses), so a test machine pointed at the Twilio
// sandbox links to the sandbox and production links to the real number. This is only the fallback when the server can't be reached.
const DEFAULT_NUMBER = '2347054272549';
let number = DEFAULT_NUMBER;

/** Called once before the page is built, with the digits the server reports (ignored unless they look like a phone number). */
export function setWhatsappNumber(digits: string | null | undefined): void {
  if (digits && /^\d{8,15}$/.test(digits)) number = digits;
}

export function whatsappHref(message: string): string {
  return `https://wa.me/${number}?text=${encodeURIComponent(message)}`;
}

export function whatsappDisplay(): string {
  // 2348000000000 -> +234 800 000 0000; other countries (the Twilio sandbox is +1) are shown as +digits.
  if (!number.startsWith('234') || number.length !== 13) return `+${number}`;
  const rest = number.slice(3);
  return `+234 ${rest.slice(0, 3)} ${rest.slice(3, 6)} ${rest.slice(6)}`;
}
