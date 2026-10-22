// The WhatsApp Business number (international format, digits only). Every WhatsApp link on the landing page reads from here.
// The server can say which number the sales assistant answers on (GET /api/webchat/status: the WebChat:WhatsAppNumber setting, else the Twilio number),
// so a test number can be switched on without rebuilding the page. The live number 0703 998 6047 is the fallback.
const DEFAULT_NUMBER = '2347039986047';
let number = DEFAULT_NUMBER;

/** Called once before the page is built, with the digits the server reports (ignored unless they look like a phone number). */
export function setWhatsappNumber(digits: string | null | undefined): void {
  if (digits && /^\d{8,15}$/.test(digits)) number = digits;
}

export function whatsappHref(message: string): string {
  return `https://wa.me/${number}?text=${encodeURIComponent(message)}`;
}

export function whatsappDisplay(): string {
  // 2348000000000 -> +234 800 000 0000; other countries are shown as +digits.
  if (!number.startsWith('234') || number.length !== 13) return `+${number}`;
  const rest = number.slice(3);
  return `+234 ${rest.slice(0, 3)} ${rest.slice(3, 6)} ${rest.slice(6)}`;
}
