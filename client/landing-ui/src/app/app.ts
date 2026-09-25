import { Component } from '@angular/core';
import { ChatWidget } from './chat/chat-widget';
import { FeedScene } from './sections/feed-scene/feed-scene';
import { Nutrition } from './sections/nutrition/nutrition';
import { ProductList } from './sections/product-list/product-list';
import { Team } from './sections/team/team';
import { Testimonials } from './sections/testimonials/testimonials';
import { RevealDirective } from './core/reveal.directive';
import { whatsappDisplay, whatsappHref } from './core/whatsapp';

@Component({
  selector: 'app-root',
  imports: [ChatWidget, FeedScene, Nutrition, ProductList, Team, Testimonials, RevealDirective],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly whatsappHref = whatsappHref("Hi Chewy Pet! I'd like to order some dog food.");
  protected readonly whatsappDisplay = whatsappDisplay;
}
