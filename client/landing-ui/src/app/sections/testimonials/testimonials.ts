import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';

// Deliberately no invented customer names/quotes here — publishing fabricated reviews
// on a real business's site is misleading. Swap this placeholder for real, attributed
// reviews (with the customer's permission) once a few have come in.
@Component({
  selector: 'app-testimonials',
  imports: [RevealDirective],
  templateUrl: './testimonials.html',
  styleUrl: './testimonials.scss',
})
export class Testimonials {}
