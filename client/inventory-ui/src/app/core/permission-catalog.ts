/**
 * Every permission the API checks (Inventory.Domain.Permissions), in plain words, grouped the way the menu is.
 * kind: 'see' opens a screen, 'act' lets the person change something there.
 */
export interface PermissionInfo { code: string; label: string; kind: 'see' | 'act'; }
export interface PermissionGroup { title: string; items: PermissionInfo[]; }

const see = (code: string, label: string): PermissionInfo => ({ code, label, kind: 'see' });
const act = (code: string, label: string): PermissionInfo => ({ code, label, kind: 'act' });

export const PERMISSION_GROUPS: PermissionGroup[] = [
  { title: 'Products & stock', items: [
    see('products.view', 'See products'),
    act('products.create', 'Add products'),
    act('products.edit', 'Edit products and their pictures'),
    act('products.delete', 'Delete products'),
    see('cost.view', 'See cost prices, margins and profit'),
    act('catalog.setup', 'Set up categories and warehouses'),
    see('stock.view', 'See stock levels'),
    act('stock.receive', 'Record production and receive stock'),
    act('stock.correct', 'Correct a stock entry'),
    act('stock.delete', 'Delete a stock entry'),
    act('stock.transfer', 'Move stock between warehouses'),
    act('stock.adjust', 'Adjust and tidy stock (write-ups and write-offs)'),
    act('serials.manage', 'Record serial numbers'),
    act('serials.writeoff', 'Write off serial-numbered items'),
    see('pricebook.view', 'See the price book'),
    act('pricebook.edit', 'Change prices in the price book'),
  ] },
  { title: 'Sales & customers', items: [
    see('sales.view', 'See sales'),
    act('sales.create', 'Make sales'),
    act('sales.edit', 'Edit a sale'),
    act('sales.void', 'Void a sale'),
    act('sales.delete', 'Delete a sale'),
    act('quotations.create', 'Create quotations'),
    act('quotations.delete', 'Delete quotations'),
    act('waybills.manage', 'Create and print waybills'),
    act('waybills.edit', 'Edit a waybill'),
    see('customers.view', 'See customers, balances and debtors'),
    act('customers.create', 'Add customers'),
    act('customers.edit', 'Edit customers'),
    act('customers.delete', 'Delete customers'),
    act('customers.payments', 'Record customer payments'),
    act('rebates.manage', 'Manage rebates'),
    act('partners.manage', 'Review and approve trade partners'),
  ] },
  { title: 'Purchasing', items: [
    act('suppliers.manage', 'Manage suppliers and their item lists'),
    act('suppliers.delete', 'Delete suppliers'),
    see('purchases.view', 'See purchases'),
    act('purchases.create', 'Buy from suppliers'),
    act('purchases.edit', 'Edit a purchase'),
    act('purchases.receive', 'Receive purchased goods'),
    act('purchases.pay', 'Pay suppliers'),
    act('purchases.delete', 'Delete or clear purchases'),
  ] },
  { title: 'Money', items: [
    see('finance.view', 'See finance: profit & loss, income, ledger'),
    act('expenses.manage', 'Record expenses'),
    act('expenses.delete', 'Delete expenses'),
    see('gl.view', 'See the accounts and statements'),
    act('gl.journal.create', 'Prepare journals'),
    act('gl.journal.approve', 'Approve journals'),
    act('gl.period.close', 'Close and lock months'),
    act('tax.manage', 'File VAT and remit withholding tax'),
    act('bank.reconcile', 'Reconcile bank statements'),
    act('approvals.decide', 'Approve large voids and write-offs'),
  ] },
  { title: 'Marketing', items: [
    act('campaigns.manage', 'Campaigns: set up and preview'),
    act('campaigns.run', 'Campaigns: run one now'),
    act('sitecontent.manage', 'Website: reviews and partner logos'),
    act('social.manage', 'Social Marketing: plan posts'),
    act('emailmarketing.manage', 'Email Marketing'),
    act('sms.manage', 'SMS Marketing'),
    act('whatsapp.manage', 'WhatsApp broadcasts'),
    act('automation.manage', 'Marketing Automation rules'),
    act('surveys.manage', 'Surveys'),
    act('blog.manage', 'Blog'),
    act('marketing.publish', 'Put marketing live without approval'),
    act('marketing.approve', 'Approve or reject marketing'),
  ] },
  { title: 'People', items: [
    act('attendance.self', 'Clock in and out'),
    see('attendance.view', 'See the team and attendance'),
    act('employees.manage', 'Manage staff records'),
    act('employees.delete', 'Delete staff records'),
    act('payroll.manage', 'Prepare payroll'),
    act('payroll.delete', 'Delete payroll runs and loans'),
    act('timeoff.manage', 'Time Off: record and approve leave'),
  ] },
  { title: 'Other apps', items: [
    act('sign.manage', 'Sign'),
    act('spreadsheet.manage', 'Spreadsheet'),
    act('esg.manage', 'ESG'),
    act('recruitment.manage', 'Recruitment'),
    act('appraisals.manage', 'Appraisals'),
    act('fleet.manage', 'Fleet'),
    act('website.manage', 'Website Builder'),
    act('events.manage', 'Events'),
    act('elearning.manage', 'eLearning'),
    act('forum.manage', 'Forum'),
  ] },
  { title: 'Documents, AI & reports', items: [
    see('documents.view', 'See and print documents'),
    act('documents.email', 'Email and WhatsApp documents to customers'),
    see('analytics.view', 'See analytics and send daily alerts'),
    act('ai.ask', 'Ask the AI assistant'),
  ] },
  { title: 'Administration', items: [
    act('users.manage', 'Manage people, roles and access'),
    see('audit.view', 'See the activity log'),
    act('company.edit', 'Edit company details, branches and delivery zones'),
    act('company.delete', 'Remove company logos and delivery zones'),
    act('storage.manage', 'Manage database storage'),
  ] },
];

/** The permission behind each record-keeping app (same table as the API's Permissions.ForApp). */
export const APP_PERMISSION: Record<string, string> = {
  sign: 'sign.manage', spreadsheet: 'spreadsheet.manage', esg: 'esg.manage', 'time-off': 'timeoff.manage', recruitment: 'recruitment.manage',
  appraisals: 'appraisals.manage', fleet: 'fleet.manage', website: 'website.manage', events: 'events.manage', blog: 'blog.manage', elearning: 'elearning.manage',
  forum: 'forum.manage', 'social-marketing': 'social.manage', surveys: 'surveys.manage', 'email-marketing': 'emailmarketing.manage', 'sms-marketing': 'sms.manage',
  whatsapp: 'whatsapp.manage', 'marketing-automation': 'automation.manage',
};
