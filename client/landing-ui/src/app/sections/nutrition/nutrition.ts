import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';

interface NutritionPoint {
  title: string;
  desc: string;
  icon: 'protein' | 'stages' | 'bowl';
}

@Component({
  selector: 'app-nutrition',
  imports: [RevealDirective],
  templateUrl: './nutrition.html',
  styleUrl: './nutrition.scss',
})
export class Nutrition {
  protected readonly points: NutritionPoint[] = [
    {
      title: '32% Protein Recipes',
      desc: 'Real meat first on every label, for lean muscle and steady energy all day long.',
      icon: 'protein',
    },
    {
      title: 'Balanced For Every Life Stage',
      desc: 'From playful puppies to senior companions — a formula tuned to every age and size.',
      icon: 'stages',
    },
    {
      title: 'Made For Picky Eaters',
      desc: "Recipes dogs actually finish — no coaxing, no leftovers going stale in the bowl.",
      icon: 'bowl',
    },
  ];

  protected readonly certs = ['32% Protein', 'All Life Stages', 'Milled & Packed Locally', 'Real Person, Not a Bot'];
}
