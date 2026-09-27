import { Component, signal } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

interface Product {
  num: string;
  title: string;
  desc: string;
}

// Real product photography only (no stock) — bags we actually have shots of fall back
// to the hero pack shot rather than a fabricated per-SKU photo.
const PRODUCT_IMAGES: Record<string, string> = {
  '01': '/chewy-bag-puppy.jpg',
  '03': '/chewy-bag-all-life-stages.jpg',
};

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

  // Defaults open on the flagship "All Life Stages" bag.
  protected readonly activeNum = signal('03');

  protected setActive(num: string): void {
    this.activeNum.set(num);
  }

  // Only SKUs we have real bag photography for get a thumbnail — no fallback/stock image.
  protected productImage(num: string): string | null {
    return PRODUCT_IMAGES[num] ?? null;
  }
}
