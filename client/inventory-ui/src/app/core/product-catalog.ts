/**
 * The record-keeping apps this business has bought, as they appear in the menu. Each opens at /apps/{key} on the shared records screen
 * (features/apps-engine), except Spreadsheet (its own grid) and Time Off (its own page). Which of them are switched on comes from the server
 * (GET /api/apps/enabled); the server holds each app's real definition (Inventory.Application.AppsEngine).
 */
export interface AppEntry { key: string; label: string; icon: string; group: string; path?: string; }
export interface AppGroup { group: string; icon: string; }

/** Menu sections an app can sit in. A section that doesn't exist yet is created just before System. */
export const APP_GROUPS: AppGroup[] = [
  { group: 'Money', icon: 'finance' }, { group: 'People', icon: 'users' }, { group: 'Websites', icon: 'spark' }, { group: 'Marketing', icon: 'gift' },
];

export const APPS: AppEntry[] = [
  { key: 'sign', label: 'Sign', icon: 'edit', group: 'Money' },
  { key: 'spreadsheet', label: 'Spreadsheet', icon: 'file', group: 'Money' },
  { key: 'esg', label: 'ESG', icon: 'spark', group: 'Money' },
  { key: 'time-off', label: 'Time Off', icon: 'clock', group: 'People', path: '/time-off' },
  { key: 'recruitment', label: 'Recruitment', icon: 'users', group: 'People' },
  { key: 'appraisals', label: 'Appraisals', icon: 'audit', group: 'People' },
  { key: 'fleet', label: 'Fleet', icon: 'supplier', group: 'People' },
  { key: 'website', label: 'Website Builder', icon: 'file', group: 'Websites' },
  { key: 'events', label: 'Events', icon: 'clock', group: 'Websites' },
  { key: 'blog', label: 'Blog', icon: 'edit', group: 'Websites' },
  { key: 'elearning', label: 'eLearning', icon: 'users', group: 'Websites' },
  { key: 'forum', label: 'Forum', icon: 'customer', group: 'Websites' },
  { key: 'social-marketing', label: 'Social Marketing', icon: 'spark', group: 'Marketing' },
  { key: 'email-marketing', label: 'Email Marketing', icon: 'file', group: 'Marketing' },
  { key: 'sms-marketing', label: 'SMS Marketing', icon: 'sale', group: 'Marketing' },
  { key: 'whatsapp', label: 'WhatsApp', icon: 'customer', group: 'Marketing' },
  { key: 'marketing-automation', label: 'Marketing Automation', icon: 'swap', group: 'Marketing' },
  { key: 'surveys', label: 'Surveys', icon: 'check', group: 'Marketing' },
];