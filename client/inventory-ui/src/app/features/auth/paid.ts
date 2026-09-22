import { ChangeDetectionStrategy, Component } from '@angular/core';

/** Where Paystack sends a customer after they pay. Public (no sign-in): the payment itself is recorded by the server, not by this page. */
@Component({
  selector: 'app-paid',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="wrap">
      <div class="card card-pad">
        <span class="eyebrow">Payment</span>
        <h1 class="display">Thank you</h1>
        <p>Your payment has been received and the business has been told. You can close this page.</p>
        <p class="muted sm">If your payment doesn't show up within a few minutes, contact the business with your bank's receipt.</p>
      </div>
    </main>`,
  styles: `.wrap { min-height: 100dvh; display: grid; place-items: center; padding: 1rem; background: var(--brand-tint); } .card { max-width: 26rem; text-align: center; } h1 { font-size: 3rem; margin: .2rem 0 .6rem; } .sm { font-size: .8rem; }`,
})
export class PaidPage {}
