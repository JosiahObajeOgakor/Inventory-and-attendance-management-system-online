import { Directive, ElementRef, afterNextRender, inject } from '@angular/core';

/** Adds `.is-visible` the first time the host element scrolls into view. Paired with the
 * `.reveal` CSS class (see styles.scss) for a fade/rise-in; a no-op when IntersectionObserver
 * isn't available (SSR, old browsers) so content is never stuck hidden. */
@Directive({
  selector: '[appReveal]',
})
export class RevealDirective {
  private readonly elementRef = inject(ElementRef<HTMLElement>);

  constructor() {
    afterNextRender(() => {
      const el = this.elementRef.nativeElement;
      if (!('IntersectionObserver' in window)) {
        el.classList.add('is-visible');
        return;
      }
      const observer = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            if (entry.isIntersecting) {
              entry.target.classList.add('is-visible');
              observer.unobserve(entry.target);
            }
          }
        },
        { threshold: 0.15, rootMargin: '0px 0px -60px 0px' },
      );
      observer.observe(el);
    });
  }
}
