import { Component } from '@angular/core';
import { ChatWidget } from './chat/chat-widget';

// The shop's WhatsApp Business number (international format, digits only): the number connected to the sales assistant
// in Twilio. Everything else on the page reads from this one constant.
const WHATSAPP_NUMBER = '2347054272549';
// What the customer's first message says. Kept to a plain greeting plus what they want, which is what the sales assistant understands best.
const WHATSAPP_MESSAGE = "Hi! I'd like to order dog food.";

@Component({
  selector: 'app-root',
  imports: [ChatWidget],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly whatsappHref =
    `https://wa.me/${WHATSAPP_NUMBER}?text=${encodeURIComponent(WHATSAPP_MESSAGE)}`;
  protected readonly whatsappDisplay = formatDisplay(WHATSAPP_NUMBER);
}

function formatDisplay(number: string): string {
  // 2348000000000 -> +234 800 000 0000
  const cc = number.slice(0, 3);
  const rest = number.slice(3);
  return `+${cc} ${rest.slice(0, 3)} ${rest.slice(3, 6)} ${rest.slice(6)}`;
}
