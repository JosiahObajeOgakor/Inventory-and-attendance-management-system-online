import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

interface NutritionPoint {
  title: string;
  desc: string;
  icon: 'protein' | 'stages';
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
  ];

  protected readonly certs = ['32% Protein', 'All Life Stages', 'Locally Produced', 'Real Person, Not a Bot'];

  protected whatsappLink(title: string): string {
    return whatsappHref(`Hi! Tell me more about "${title}".`);
  }
}
