import { Component, afterNextRender, computed, inject, signal } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { Stores as StoreList, waLink } from '../../core/stores';
import { whatsappHref } from '../../core/whatsapp';
import { Shop } from '../shop/shop';

/**
 * "Shop with us": two short questions instead of a wall of products. First, would you like to see what's in stock (to buy or enquire)? Then,
 * under it, would you like our sister store, Candid Purrfect, for a wider range of foods and accessories? Each answer opens that store's live shelf.
 * Links to #shop open the first; #candid opens both.
 */
@Component({
  selector: 'app-stores',
  imports: [RevealDirective, Shop],
  templateUrl: './stores.html',
  styleUrl: './stores.scss',
})
export class StoresSection {
  protected readonly stores = inject(StoreList);
  protected readonly mainOpen = signal(false);
  protected readonly sisterOpen = signal(false);
  /** The landing brand goes by its brand name on the website, whatever the business is called inside the stock system. */
  protected readonly main = computed(() => { const s = this.stores.find('chewypets'); return s ? { ...s, name: 'Chewy Pet' } : null; });
  protected readonly sister = computed(() => this.stores.find('candid') ?? null);
  protected readonly enquiryHref = whatsappHref("Hi! I have a question about your products.");
  protected readonly sisterEnquiryHref = computed(() => {
    const s = this.sister();
    return s?.whatsApp ? waLink(s.whatsApp, `Hi ${s.name}! I have a question about your products.`) : whatsappHref('Hi! I have a question about Candid Purrfect.');
  });

  constructor() {
    afterNextRender(() => {
      const follow = () => {
        const h = location.hash;
        if (h === '#shop' || h === '#stores') this.openMain();
        if (h === '#candid') { this.openMain(); this.openSister(); }
      };
      follow();
      window.addEventListener('hashchange', follow);
    });
  }

  protected openMain(): void { this.mainOpen.set(true); }
  protected openSister(): void {
    this.sisterOpen.set(true);
    setTimeout(() => document.getElementById('candid-shelf')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  }
}
