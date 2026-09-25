// TODO: swap in the real WhatsApp Business number (international format, digits only)
// before this ships. Every WhatsApp link on the landing page reads from this one constant.
const WHATSAPP_NUMBER = '2348000000000';

export function whatsappHref(message: string): string {
  return `https://wa.me/${WHATSAPP_NUMBER}?text=${encodeURIComponent(message)}`;
}

export const whatsappDisplay = formatDisplay(WHATSAPP_NUMBER);

function formatDisplay(number: string): string {
  // 2348000000000 -> +234 800 000 0000
  const cc = number.slice(0, 3);
  const rest = number.slice(3);
  return `+${cc} ${rest.slice(0, 3)} ${rest.slice(3, 6)} ${rest.slice(6)}`;
}
