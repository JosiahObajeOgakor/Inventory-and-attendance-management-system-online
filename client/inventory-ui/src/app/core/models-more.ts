import { Paged, SaleLine } from './models';

// ---- company profile ----
export interface Bank { bankName: string; accountName: string; accountNumber: string; }
export interface CompanyProfile {
  legalName: string; address: string; phone: string; email: string; taxId: string; defaultVatRate: number; defaultRebateRatePct: number;
  banks: Bank[]; assets: string[]; hasPriceLists: boolean; buysGoods: boolean;
}
export type AssetKind = 'logo' | 'signature' | 'waybill-stamp';

// ---- quotations & waybills ----
export interface QuotationRow { id: number; quotationNumber: string; customer: string; quotationDate: string; totalAmount: number; status: string; preparedBy: string; convertedInvoiceId: number | null; }
export interface QuotationDetail {
  id: number; quotationNumber: string; customerId: number; customer: string; customerType: string; quotationDate: string; subtotal: number; discountPct: number; discountAmount: number;
  vatRate: number; vatAmount: number; totalAmount: number; priceTier: string; status: string; convertedInvoiceId: number | null; preparedBy: string;
  items: { productId: number; product: string; sku: string; unit: string; quantity: number; unitPrice: number; lineTotal: number }[];
}
export interface QuoteRequest { customerId: number; quoteDate?: string | null; priceTier: string; discountPct: number; vatRate: number; lines: SaleLine[]; }
export interface QuoteResult { id: number; number: string; subtotal: number; discountAmount: number; vatAmount: number; total: number; }
export interface WaybillRow { id: number; waybillNumber: string; issueDate: string; invoiceId: number; invoiceNumber: string; customer: string; driverName: string | null; vehiclePlate: string | null; }
export interface PendingInvoice { invoiceId: number; invoiceNumber: string; invoiceDate: string; customer: string; warehouse: string | null; totalAmount: number; waybillCount: number; }
export interface WaybillPrefill { invoiceNumber: string; customer: string; phone: string; address: string; warehouse: string; }
export interface WaybillInput { invoiceId: number; issueDate?: string | null; driverName: string | null; driverPhone: string | null; vehiclePlate: string | null; destinationAddress: string | null; notes: string | null; }

// ---- attendance ----
export interface AttendanceToday { firstCheckInAt: string | null; lastCheckOutAt: string | null; checkIns: number; lastCheckInAt: string | null; }
export interface AttendanceEvent { id: number; fullName: string; workDate: string; at: string; event: string; }
export interface AttendanceDay { userId: number; fullName: string; workDate: string; firstIn: string | null; lastOut: string | null; checkIns: number; }

// ---- employees & payroll ----
export interface Employee { id: number; fullName: string; position: string; phone: string | null; startedOn: string; monthlySalary: number; isActive: boolean; openLoanBalance: number; payrollMonths: number; loans: number; }
export interface EmployeeInput { fullName: string; position: string; phone: string | null; startedOn: string | null; monthlySalary: number; }
export interface PayrollRow { id: number; employeeId: number; fullName: string; position: string; periodYear: number; periodMonth: number; salaryAmount: number; loanDeduction: number; netPay: number; paid: boolean; paidDate: string | null; note: string | null; }
export interface Loan { id: number; loanDate: string; principal: number; balance: number; closed: boolean; note: string | null; repayments: { paidDate: string; amount: number; note: string | null }[]; }

// ---- serials ----
export interface Serial { id: number; serialNumber: string; status: string; warehouseId: number | null; warehouse: string | null; receivedAt: string; soldAt: string | null; invoiceNumber: string | null; customer: string | null; notes: string | null; }
export interface SerialDiscrepancy { productId: number; sku: string; product: string; countedStock: number; serialsOnShelf: number; difference: number; }

// ---- expenses & rebates ----
export interface ExpenseRow { id: number; category: string; expenseDate: string; amount: number; note: string | null; }
export interface ExpenseList { page: Paged<ExpenseRow>; total: number; byCategory: { category: string; total: number }[]; }
export interface ExpenseInput { category: string; expenseDate: string | null; amount: number; note: string | null; }
export interface RebateCustomer { customerId: number; customer: string; ranking: string; available: number; redeemed: number; lifetime: number; }
export interface RebateSummary { customers: RebateCustomer[]; outstandingTotal: number; redeemedTotal: number; defaultRatePct: number; }
export interface RebateEntry { id: number; entryDate: string; invoiceNumber: string | null; amount: number; status: string; redeemedDate: string | null; note: string | null; }

// ---- price book ----
export interface PriceBookRow { productId: number; category: string; product: string; sku: string; unit: string; distributor: number; wholesaler: number; retail: number; inStock: number; lastChanged: string | null; }
export interface PriceBook { page: Paged<PriceBookRow>; lastUpdated: string | null; changesThisMonth: number; }
export interface PriceHistory {
  id: number; changedAt: string; product: string; distributorWas: number; distributorNow: number; wholesalerWas: number; wholesalerNow: number;
  retailWas: number; retailNow: number; changedBy: string; note: string;
}

// ---- customer metrics ----
export interface CustomerMetrics {
  customerId: number; name: string; ranking: string; trailingTwelveMonths: number; lifetimeSpend: number; invoiceCount: number; averageOrder: number;
  lastPurchase: string | null; balance: number; monthly: { year: number; month: number; total: number; invoices: number }[];
  topProducts: { productId: number; product: string; quantity: number; revenue: number }[];
}
