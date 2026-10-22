import { I18n } from '../../core/i18n';
import { Component, computed, inject } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { Stores } from '../../core/stores';
import { whatsappHref } from '../../core/whatsapp';

/** Delivery areas, fees and usual times, opening hours and the reply-time promise: all entered by staff in the inventory app, so the site never goes out of date. */
@Component({
  selector: 'app-service-info',
  imports: [RevealDirective],
  templateUrl: './service-info.html',
  styleUrl: './service-info.scss',
})
export class ServiceInfo {
  protected readonly t = inject(I18n).t;
  private readonly stores = inject(Stores);
  protected readonly main = computed(() => this.stores.find('chewypets') ?? null);
  protected readonly zones = computed(() => this.main()?.delivery ?? []);
  protected readonly askHref = whatsappHref('Hi! Do you deliver to my area, and how much is delivery?');
  /** A zone saved with ₦0 usually means the fee isn't set yet, so it is never advertised as free. */
  protected naira(n: number): string { return n === 0 ? 'Ask us' : '₦' + n.toLocaleString('en-NG', { maximumFractionDigits: 0 }); }
}