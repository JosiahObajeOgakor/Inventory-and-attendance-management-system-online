// Shapes returned by the ASP.NET Core API (System.Text.Json, camelCase). Money is a JSON number; DateOnly is "yyyy-MM-dd".

export type Role = 'ADMIN' | 'CLERK';

export interface CompanyChoice { key: string; displayName: string; hasPriceLists?: boolean; buysGoods?: boolean; }
export interface Me {
  id: number; username: string; fullName: string; role: Role; company: string;
  companies: CompanyChoice[]; mustChangePassword: boolean;
}

export interface Paged<T> { items: T[]; total: number; page: number; pageSize: number; }
export interface PageQuery { page?: number; pageSize?: number; search?: string; }

export interface Product {
  id: number; sku: string; name: string; categoryId: number; category: string; unit: string; reorderLevel: number;
  costPrice: number | null;                       // null for clerks (hidden by the server)
  priceDistributor: number; priceWholesaler: number; priceRetail: number;
  barcode: string | null; tracksSerial: boolean; isActive: boolean; totalQuantity: number;
}
export interface ProductInput {
  sku: string; name: string; categoryId: number; unit: string; reorderLevel: number; costPrice: number;
  priceDistributor: number; priceWholesaler: number; priceRetail: number; barcode: string | null; tracksSerial: boolean;
}
export interface NewProductRequest {
  product: ProductInput; openingQuantity: number; warehouseId: number; batchNumber: string | null; expiryDate: string | null;
}
export interface Category { id: number; name: string; }
export interface Warehouse { id: number; name: string; location: string | null; }

export type BatchStatus = 'OK' | 'Low stock' | 'Expiring soon';
export interface BatchRow {
  batchId: number; productId: number; sku: string; product: string; category: string; warehouseId: number; warehouse: string;
  batchNumber: string; expiryDate: string | null; quantity: number; reorderLevel: number; status: BatchStatus;
}
export interface MovementRow {
  id: number; at: string; productId: number; product: string; sku: string; warehouse: string; type: 'IN' | 'OUT' | 'ADJUST';
  quantity: number; referenceType: string | null; referenceId: number | null; userId: number; user: string | null; note: string | null;
}

export interface Supplier {
  id: number; name: string; category: string | null; contactName: string | null; phone: string | null; email: string | null;
  address: string | null; taxId: string | null; balance: number;
}
export type SupplierInput = Omit<Supplier, 'id' | 'balance'>;

export type CustomerType = 'Distributor' | 'Wholesaler' | 'Retailer' | 'Walk-in';
export interface Customer {
  id: number; name: string; customerType: CustomerType; contactName: string | null; phone: string | null; location: string | null;
  address: string | null; email: string | null; taxId: string | null; rebateRatePct: number; creditLimit: number; balance: number;
  trailingTwelveMonthSpend: number; ranking: 'Gold' | 'Silver' | 'Bronze';
}
export interface CustomerLookup { id: number; name: string; customerType: CustomerType; phone: string | null; balance: number; }
export type CustomerInput = Omit<Customer, 'id' | 'balance' | 'trailingTwelveMonthSpend' | 'ranking'>;

export interface InvoiceRow {
  id: number; invoiceNumber: string; customer: string; invoiceDate: string; paymentMethod: string; totalAmount: number;
  status: string; estProfit: number | null;
}
export interface InvoiceItem {
  productId: number; product: string; sku: string; unit: string; quantity: number; unitPrice: number; lineTotal: number; unitCost: number | null;
}
export interface InvoiceDetail {
  id: number; invoiceNumber: string; customerId: number; customer: string; customerType: string; invoiceDate: string; dueDate: string | null;
  subtotal: number; discountPct: number; discountAmount: number; vatRate: number; vatAmount: number; totalAmount: number; amountPaid: number;
  status: string; paymentMethod: string; priceTier: string; warehouseId: number | null; createdBy: string; voidReason: string | null;
  items: InvoiceItem[]; payments: { at: string; amount: number; method: string }[];
}

export interface SaleLine { productId: number; quantity: number; unitPrice: number; serials?: string[]; }
export interface SaleRequest {
  customerId: number; saleDate?: string | null; priceTier: string; warehouseId: number; paymentMethod: string;
  discountPct: number; vatRate: number; paidNow: number; dueDate?: string | null; lines: SaleLine[];
}
export interface SaleResult {
  invoiceId: number; invoiceNumber: string; subtotal: number; discountAmount: number; vatAmount: number; total: number;
  outstanding: number; status: string; previousBalance: number; appliedToPreviousBalance: number; remainingBalance: number;
}
export interface SalePreview {
  subtotal: number; discountAmount: number; vatAmount: number; total: number; previousBalance: number;
  appliedToInvoice: number; appliedToPreviousBalance: number; outstanding: number; status: string;
}

export interface PurchaseRow { id: number; poNumber: string; supplier: string; orderDate: string; status: string; paymentStatus: string; totalAmount: number; amountPaid: number; }
export interface PurchaseDetail {
  id: number; poNumber: string; supplierId: number; supplier: string; orderDate: string; status: string; paymentStatus: string;
  totalAmount: number; amountPaid: number; items: { productId: number; product: string; sku: string; quantity: number; unitCost: number; lineTotal: number }[];
}
export interface PurchaseRequest {
  supplierId: number; orderDate?: string | null; vatRate: number; receiveNow: boolean; warehouseId: number; paidNow: number; paymentMethod: string;
  lines: { productId: number; quantity: number; unitCost: number }[];
}
export interface PurchaseResult {
  purchaseOrderId: number; poNumber: string; subtotal: number; vatAmount: number; total: number; outstanding: number; status: string;
  previousBalance: number; appliedToPreviousBalance: number; remainingBalance: number;
}

export interface ReorderAdvice {
  productId: number; product: string; onHand: number; reorderLevel: number; avgDailyDemand: number; daysOfCover: number | null;
  reorderPoint: number; suggestedOrderQty: number; urgency: 'Out of stock' | 'Reorder now' | 'Low stock' | 'OK'; summary: string;
}
export interface Dashboard {
  stockValue: number; lowStockCount: number; todaysInvoices: number; recentInvoices: InvoiceRow[]; lowStock: BatchRow[];
  grossProfitThisMonth: number | null; expensesThisMonth: number | null; reorder: ReorderAdvice[] | null;
}

export interface FinanceSummary {
  year: number; month: number; revenue: number; discounts: number; cogs: number; grossProfit: number; expenses: number; netProfit: number;
  accountsPayable: number; accountsReceivable: number; rebatesAvailable: number;
}
export interface MonthlyIncome { year: number; month: number; invoices: number; grossSales: number; vat: number; netSales: number; collected: number; outstanding: number; }
export interface LedgerRow { id: number; entryDate: string; accountName: string; accountType: string; entryType: 'Debit' | 'Credit'; amount: number; reference: string | null; }

export interface UserRow {
  id: number; username: string; fullName: string; role: Role; isActive: boolean; mustChangePassword: boolean; companyAccess: string; lastLoginAt: string | null;
}
export interface AuditRow { id: number; at: string; user: string; action: string; entity: string; entityId: string | null; detail: string | null; }

/** RFC 7807 problem details as sent by the API. */
export interface Problem {
  title?: string; detail?: string; status?: number; errors?: Record<string, string[]>;
  shortfalls?: { productId: number; productName: string; requested: number; available: number; shortBy: number; advice: string }[];
}

export interface LoginAuditRow { id: number; username: string; succeeded: boolean; reason: string | null; clientAddress: string | null; atUtc: string; }
