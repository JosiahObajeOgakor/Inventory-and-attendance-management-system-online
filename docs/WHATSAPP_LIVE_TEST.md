# WhatsApp live test — `+234 705 427 2549`

What to set on the server, what to type from your phone, and what should happen. Everything here is deterministic text; the AI is only a fallback (see "Credits").

## 1. Server settings (`/etc/inventory/api.env`)

| Setting | Value |
|---|---|
| `Twilio__AccountSid`, `Twilio__AuthToken` | from the Twilio console |
| `Twilio__PublicBaseUrl` | `https://chewypetsfeeds.com` (no trailing slash) |
| `Twilio__Numbers__0__From` / `__CompanyKey` | `+2347054272549` / `chewypets` |
| `Notify__AdminPhone` | the admin's own WhatsApp number (e.g. `08012345678`). **Only this number gets the "sale completed" WhatsApp.** |
| `Notify__AdminEmail` | the admin's email (gets every sale email) |
| `Smtp__*` | the shop mailbox (customer, admin and clerk emails are sent from it) |
| `Twilio__Templates__AdminSale` | optional: an approved template (`HX…`) with 5 variables: 1 invoice, 2 buyer, 3 total, 4 items, 5 channel. Needed only when the admin has not written to the business number in the last 24 hours (WhatsApp lets a business send free text only inside that window). |
| `RateLimit__WhatsAppPerMinute` | default `1200` (20 messages a second for the whole shop). It was 120. |
| `AlatPay__*` | live keys: customers get a one-use bank account in the chat |
| `Paystack__SecretKey` (optional) | when set, customers ALSO get a secure card / USSD / bank link in the chat |

Twilio console → the number → "When a message comes in" → `https://chewypetsfeeds.com/api/twilio/whatsapp` (POST).

**Clerks** get the sale **email only**: add each clerk's email under *People & access* (the new Email box). They are never sent WhatsApp.

## 2. What to type (from any phone)

| You type | Expect |
|---|---|
| `hi` | welcome and an example product from your own stock |
| `products` | the in-stock list with prices (your tier's prices if you are a registered wholesaler/distributor) |
| `2 adult dog food` | a summary with the total; then `yes` → a one-use bank account (and a card link if Paystack is set) |
| pay the exact amount | confirmation in the same chat, in the language you wrote |
| `abeg i wan buy 2 …` / `ndewo achọrọ m 2 …` / `sannu ina son 2 …` / `jọwọ mo fẹ́ ra 2 …` | Pidgin / Igbo / Hausa / Yoruba replies |
| `quote 50 adult dog food` | a quotation summary; `send` → the quotation in the chat; type an email address and the PDF is emailed too; then `yes` buys the same items |
| `where is my waybill` | your last three orders with waybill number, date and driver (only for the number you are writing from) |
| `STOP` / `SUBSCRIBE` | opt out / in of reminders and offers |

Stock is counted across **all** warehouses and branches. The sale is booked to the default warehouse when it can fill the order, otherwise to the first warehouse that can; the units themselves leave the earliest-expiring stock, and the alert lists exactly which warehouses they left.

## 3. When a sale is completed (payment confirmed)

* Stock is deducted and the invoice made (same for WhatsApp and the website chat).
* **Admin WhatsApp** (`Notify__AdminPhone`): buyer name, phone, email, type; items; total; how it was paid; which warehouses the stock left; branch; time.
* **Emails** with the receipt PDF: the customer (if we have an address), the admin, and every active clerk who works in this business and has an email.
* The dashboard card **Chat requests** counts chats started, orders, quotations asked, delivery asks, completed sales and hand-overs, for WhatsApp and the website together.

## 4. Credits

* A normal order, quotation or delivery question uses **no AI call**: product names, quantities, yes/no, "I have paid", quote, waybill and language are plain code.
* The AI is asked only when the words could not be understood (heavy slang, an unwritten language) and never more than 10 times per sale (`SalesAssistant__MaxAiCallsPerSale`).
* Each WhatsApp message in or out is billed by Twilio and Meta; replies to a customer's message are sent in the HTTP response, so a normal chat costs one inbound plus one outbound message per turn. The admin alert is one more outbound message per completed sale.

## 5. Measured speed (local machine, signed webhook, deterministic messages)

| Concurrent customers | Requests | Throughput | p50 | p95 | p99 |
|---|---|---|---|---|---|
| 1 | 15 | 6.5/s | 109 ms | 749 ms (first call) | 749 ms |
| 5 | 75 | 24/s | 219 ms | 318 ms | 324 ms |
| 10 | 150 | 30/s | 296 ms | 528 ms | 546 ms |
| 20 | 300 | 30/s | 617 ms | 1,403 ms | 1,543 ms |
| 40 | 400 | 56/s | 582 ms | 1,263 ms | 1,267 ms |
| 80 | 800 | 56/s | 1,395 ms | 2,148 ms | 2,305 ms |

No request failed. These were taken on a development PC with MySQL in Docker; the VPS will differ. Twilio waits up to 15 seconds for a reply. Re-measure on the server after deploying.
