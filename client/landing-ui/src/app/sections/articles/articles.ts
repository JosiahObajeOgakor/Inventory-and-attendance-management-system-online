import { Component, PLATFORM_ID, inject, signal } from '@angular/core';
import { isPlatformBrowser } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { RevealDirective } from '../../core/reveal.directive';

interface Article { title: string; url: string; date: string | null; excerpt: string; }

/** Feeding guides and pet-care articles, written by staff in the Blog app. The section stays hidden until the first one is published. */
@Component({
  selector: 'app-articles',
  imports: [RevealDirective],
  templateUrl: './articles.html',
  styleUrl: './articles.scss',
})
export class Articles {
  protected readonly articles = signal<Article[]>([]);
  constructor() {
    if (!isPlatformBrowser(inject(PLATFORM_ID))) return;
    inject(HttpClient).get<Article[]>('/api/webchat/articles').subscribe({ next: a => this.articles.set(a), error: () => {} });
  }
  protected day(d: string | null): string { return d ? new Date(d).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' }) : ''; }
}