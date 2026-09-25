import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

@Component({
  selector: 'app-feed-scene',
  imports: [RevealDirective],
  templateUrl: './feed-scene.html',
  styleUrl: './feed-scene.scss',
})
export class FeedScene {
  protected readonly whatsappHref = whatsappHref("Hi! I want to order Chewy Pet food for feeding time.");
}
