import { Component } from '@angular/core';
import { RevealDirective } from '../../core/reveal.directive';
import { whatsappHref } from '../../core/whatsapp';

interface Role {
  title: string;
  desc: string;
  initials: string;
}

// Role-based, not named individuals — swap in real staff (name, photo) here once
// the client confirms who's on the WhatsApp desk day to day.
@Component({
  selector: 'app-team',
  imports: [RevealDirective],
  templateUrl: './team.html',
  styleUrl: './team.scss',
})
export class Team {
  protected readonly roles: Role[] = [
    { title: 'Nutrition Specialist', desc: "Answers what to feed and why — not a script.", initials: 'NS' },
    { title: 'Quality & Stock Lead', desc: 'Checks every batch and keeps bags moving.', initials: 'QS' },
    { title: 'Customer Care', desc: "The person actually replying on WhatsApp.", initials: 'CC' },
  ];

  protected readonly whatsappHref = whatsappHref("Hi! I'd like to speak with the Chewy Pet team.");
}
