import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

interface Product {
  num: string;
  title: string;
  desc: string;
}

@Component({
  selector: 'app-product-list',
  imports: [RevealDirective],
  templateUrl: './product-list.html',
  styleUrl: './product-list.scss',
})
export class ProductList {
  // Cats have their own line — see the Candid Purrfect cross-promo further down the page.
  protected readonly products: Product[] = [
    { num: '01', title: 'Puppy Formula', desc: 'Small kibble, nutrient-dense, built for growing bodies and first teeth.' },
    { num: '02', title: 'Adult Dog Food', desc: 'High-protein daily nutrition for active adult dogs of every breed.' },
    { num: '03', title: 'All Life Stages', desc: 'One bag that keeps working as your dog grows — 32% protein, 20% fat.' },
    { num: '04', title: 'Senior', desc: 'Gentler on ageing joints and appetites, same full bowl.' },
    { num: '05', title: 'Treats & Chews', desc: 'Reward-time snacks made to the same real-meat standard as every bag.' },
  ];

  protected orderHref(product: Product): string {
    return whatsappHref(`Hi! I'd like to order Chewy Pet ${product.title}.`);
  }
}
