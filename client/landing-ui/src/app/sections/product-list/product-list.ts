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
  '02': '/chewy-bag-all-life-stages.jpg',
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
    { num: '01', title: 'Puppy Food', desc: 'Small kibble, nutrient-dense, built for growing bodies and first teeth — 36% protein, 20% fat.' },
    { num: '02', title: 'All Life Stage', desc: 'One bag that keeps working as your dog grows — 32% protein, 20% fat.' },
  ];

  protected orderHref(product: Product): string {
    return whatsappHref(`Hi! I'd like to order Chewy Pet ${product.title}.`);
  }

  // Defaults open on the flagship "All Life Stage" bag.
  protected readonly activeNum = signal('02');

  protected setActive(num: string): void {
    this.activeNum.set(num);
  }

  // Only SKUs we have real bag photography for get a thumbnail — no fallback/stock image.
  protected productImage(num: string): string | null {
    return PRODUCT_IMAGES[num] ?? null;
  }
}
