export interface Kpi { current: number; previous: number; changePct: number | null; }
export interface DayPoint { date: string; revenue: number; collected: number; grossProfit: number; expenses: number; }
export interface MonthPoint { year: number; month: number; revenue: number; grossProfit: number; expenses: number; }
export interface WarehouseStock { id: number; name: string; units: number; costValue: number; retailValue: number; products: number; lowBatches: number; expiredUnits: number; expiringSoonUnits: number; }
export interface CustomerMonth { year: number; month: number; sales: number; grossProfit: number; profitability: number; }
export interface CustomerHero { id: number; name: string; ranking: string; openBalance: number; months: CustomerMonth[]; }
export interface DueItem { invoiceId: number; invoiceNumber: string; customerId: number; customer: string; phone: string | null; outstanding: number; dueDate: string; daysOverdue: number; }
export interface ProductMover { productId: number; name: string; quantity: number; revenue: number; daysSinceLastSale: number | null; inStock: number; stockValue: number; }
export interface Signal { kind: string; severity: 'high' | 'medium' | 'low'; title: string; detail: string; amount: number | null; }
export interface Overview {
  asOf: string; revenue: Kpi; grossProfit: Kpi; netProfit: Kpi; losses: Kpi; collected: Kpi; inventoryValue: number; customers: number; newCustomersThisMonth: number;
  receivablesTotal: number; overdueTotal: number; days: DayPoint[]; months: MonthPoint[]; warehouses: WarehouseStock[]; topCustomers: CustomerHero[];
  topProducts: ProductMover[]; slowMovers: ProductMover[]; signals: Signal[];
}
export interface CalendarData { year: number; month: number; due: DueItem[]; overdue: DueItem[]; }
export interface Advice { title: string; body: string; priority: 'high' | 'medium' | 'low'; }
export interface InsightSet { generatedAtUtc: string; fromAi: boolean; items: Advice[]; }
export interface AiStatus { configured: boolean; remaining: number; dailyLimit: number; nextAvailableUtc: string | null; }
export interface AssistantAnswer { answer: string; remaining: number; dailyLimit: number; nextAvailableUtc: string | null; limited: boolean; }
export interface ChatTurn { role: 'user' | 'assistant'; text: string; }
export interface EmailPreview { subject: string; html: string; to: string; }
export interface EmailSent { to: string; subject: string; attachment: string; }

export interface ForecastRow { productId: number; product: string; unit: string; onHand: number; weeklyDemand: number; method: string; confidence: string; summary: string; daysOfCover: number | null; runsOutOn: string | null; suggestedOrder: number; }
export interface BasketRule { antecedent: string[]; consequent: string; support: number; confidence: number; lift: number; baskets: number; }
export interface UnusualRow { movementId: number; at: string; product: string; warehouse: string; kind: string; quantity: number; typical: number; score: number; reference: string; }
export interface AdviceLine { kind: string; text: string; }
export interface SuggestedLine { productId: number; product: string; unit: string; quantity: number; onHand: number; weeklyDemand: number; runsOutOn: string | null; unitCost: number; basis: string; }

