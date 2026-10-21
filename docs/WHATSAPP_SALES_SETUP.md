# WhatsApp sales chat — Twilio + AlatPay

A customer messages the shop's WhatsApp number in English, Pidgin, Igbo, Hausa (or Igala, see below), names what they want, is shown a single-use
**bank account inside the chat**, pays by transfer, and the bot confirms in the same chat. Nothing sends them to a website.

```
Customer ──WhatsApp──▶ Twilio ──POST /api/twilio/whatsapp──▶ ChatFlowService ──▶ MySQL (stock, prices, customers)
                                                                 │  └─ (only if words fail) 1 short OpenAI call, max 10 per sale
                                                                 └─▶ AlatPay: create single-use virtual account
Customer ──bank transfer──▶ AlatPay ──POST /api/alatpay/webhook──▶ verify signature ─▶ ask AlatPay "was it paid, how much?"
        ◀──WhatsApp (Twilio REST)── "Payment received" ◀── sale created, stock deducted, receipt line items
```

## How a sale goes (and where it can't go wrong)

| Step | What happens | Source of truth |
|---|---|---|
| "abeg i wan buy 2 adult dog food" | Words are matched to real product names (typos, plurals, number words in 4 languages) | `products`, `stock_batches` |
| Quantity too high | "We only have 3 left. Should I take 3?" | stock on hand in the sale warehouse, unexpired batches, **minus stock held for other customers who already have an account** |
| Summary + "yes / no" | Price comes from the product record (retail tier), VAT from the company profile; the shown total is exactly what the bank account is generated for | `products.PriceRetail`, company profile |
| "yes" | Stock is re-checked against the database **again**, a quotation is created, AlatPay issues an account for that exact total | `quotations`, `payment_links` |
| Customer transfers | Signed webhook → we ask AlatPay itself (status + amount sent) → record once → quotation converts to a sale, stock deducts, payment recorded | single DB transaction, row-locked, idempotent |
| Confirmation | Pushed into the same chat in the customer's language; also works if the customer types "paid" first | `ChatPaymentNotifier` |

Edge cases handled: underpayment (told the exact shortfall, nothing recorded), overpayment (recorded, admin told to refund/credit), webhook retries (no double sale / no double
message), customer says "paid" before the webhook arrives (we check with AlatPay on the spot), stock vanished after payment (money parked as `PaidUnfulfilled`, admin emailed
"PAID BUT NOT FULFILLED", customer told they'll be refunded or contacted), expired account (customer starts again).

**No numbered menus.** The customer types product names and yes/no in their own words. When a word fits two products the bot lists the names and the customer types one.

## AI credit use

* A normal sale uses **zero** AI calls. Reading products, quantities, yes/no, "I have paid", cancel and the language are all plain code.
* The AI is called only when the words could not be understood (e.g. Igala or very heavy slang): one short request, no tools, JSON out, ~150 tokens.
* Hard ceiling **10 AI calls per sale** (`SalesAssistant__MaxAiCallsPerSale`), counted in the database *before* each call. After that the bot hands over to a person.
* The AI never writes a reply and never sees or sets a price. It only maps words to product ids from a list we give it; each id is re-checked against the database.
* All replies are fixed sentences (`ChatText.cs`), so wording, prices and stock numbers cannot drift.

## Languages

English, Nigerian Pidgin, Igbo and Hausa are written. **Igala is not**: I can't write it reliably, and a wrong sentence to a customer is worse than a Pidgin one.
Until a native speaker fills it in, Igala customers get Pidgin (then English), and the AI call (if enabled) still understands what they *type*.
To add Igala (or correct anything), create a JSON file and set `SalesAssistant__PhrasesFile=/path/to/phrases.json`:

```json
{
  "igl": { "welcome": "… {0} … \"2 {1}\" …", "summary": "…", "pay_instr": "…" },
  "markers": { "igl": ["words", "an Igala customer", "often writes"] }
}
```
Keep the `{0}`, `{1}` placeholders. The full list of keys is the English block in `src/Inventory.Application/SalesAssistant/Chat/ChatText.cs`.
`markers` are the words that make the bot switch a chat to that language. Yes/no/"paid" words for Igala can be added to `ChatLexicon.cs`.

## What I need from you to test on real WhatsApp

### Twilio
1. **Account SID** and **Auth Token** (Console → Account info). The token also signs the webhooks.
2. **WhatsApp sender.** Fastest: the **Twilio Sandbox for WhatsApp** (Console → Messaging → Try it out → Send a WhatsApp message).
   Give me the sandbox number (usually `+14155238886`) and the `join <two-words>` code. Every tester must send that join message from their phone once.
   Production later needs an approved WhatsApp sender (Meta business verification) — not needed to test.
3. Set the sandbox's **"When a message comes in"** URL to `https://<your-public-host>/api/twilio/whatsapp` (HTTP POST).
4. A public https URL for this PC while testing: `ngrok http 5041` (ngrok is already installed here). Put that address in `Twilio__PublicBaseUrl` — the signature covers the exact URL.
5. Sandbox rule to know: Twilio only lets us send *free-form* messages within 24 h of the customer's last message. Payment confirmations are fine (the customer has just been chatting);
   a confirmation hours later would need an approved template in production.

### AlatPay
1. **Subscription key** (`Ocp-Apim-Subscription-Key`) and **Business ID** (a GUID) — sandbox first.
2. **Webhook secret** (dashboard) and register the webhook URL `https://<your-public-host>/api/alatpay/webhook`. Turn on webhook retries.
3. Ask AlatPay support for the **sandbox simulate-transfer request body** (their docs list the URL but not the payload) so a "payment" can be triggered without moving real money.
4. Confirm with AlatPay how long a virtual account lives (their docs say both 30 minutes and 24 hours). The bot uses whatever `expiredAt` the API returns and holds stock for
   `SalesAssistant__ReservationMinutes` (default 60).

### Shop data (this is what the bot trusts)
* `Companies:chewypets:DefaultWarehouseId` must point at the warehouse sales come out of (ChewyPets is `1`; Candid Purrfect is `0`, i.e. the chat refuses to sell there until set).
* Products must be **active**, have a **retail price**, and stock **batches in that warehouse**. Serial-tracked products are not offered in chat.
* Optional: `OpenAI__ApiKey` (+ `OpenAI__Model`) if you want the last-resort AI. Without it the bot is fully deterministic and hands unreadable messages to a person.
* Optional: `Notify__AdminEmail` (+ Zoho SMTP) so you're emailed on every payment and on any "paid but not fulfilled".

## Environment variables

```
Twilio__AccountSid=AC...
Twilio__AuthToken=...
Twilio__PublicBaseUrl=https://<ngrok-or-domain>          # no trailing slash
Twilio__Numbers__0__From=+14155238886                    # sandbox, later your approved sender
Twilio__Numbers__0__CompanyKey=chewypets

AlatPay__SubscriptionKey=...
AlatPay__BusinessId=...
AlatPay__WebhookSecret=...
# AlatPay__BaseUrl=https://apibox.alatpay.ng            # default

SalesAssistant__MaxAiCallsPerSale=10
SalesAssistant__ReservationMinutes=60
SalesAssistant__PhrasesFile=                             # optional, Igala etc.
```

## Running a test conversation

1. `./scripts/dev.ps1` (starts MySQL in Docker, the API on :5041, the UI on :4200).
2. `ngrok http 5041`, set `Twilio__PublicBaseUrl` to the https address, restart the API, paste the same URLs into Twilio and AlatPay.
3. From your phone (after the sandbox `join`): `hello` → `abeg i wan buy 2 <a product name>` → `yes` → pay the account shown → wait for the confirmation.
4. Check in the UI: *Sales* shows the invoice paid by "AlatPay", *Stock* shows the deduction, *Activity log* shows `ONLINE_PAYMENT`.

## Going live (deployment)

* Everything the bot says about stock and price is read from the database at that moment — there is no catalogue copy to go stale. Keep the stock records honest
  (receive purchases, record counter sales, stock-take) and the bot is honest.
* Keep the API behind NGINX/HTTPS (`deploy/`); both webhooks are signature-checked and exempt only from the CSRF header, nothing else.
* Candid Purrfect: add a second `Twilio__Numbers__1__From` / `CompanyKey=candid` and set its `DefaultWarehouseId`.
* Rotate any key that has been pasted into a chat or ticket.
* Watch `payment_links` rows with status `PaidUnfulfilled` — each is a customer whose money you hold.

---

# Campaigns: reminders, offers and restock alerts (admin-scheduled, written fresh each run)

**Campaigns** in the app's sidebar (admin only). Four campaigns, each with an On/Off switch, a time (Lagos), "repeat every N days", optional start/end dates, and channels:

| Campaign | Who gets it | What it says (all from live records) |
|---|---|---|
| **Reorder reminders** | Opted-in customers whose last purchase of a product is due, judged by *their own* rhythm (median gap between their purchases; default 21 days if unknown) | "You bought 2 × Adult Dog Food 20kg 24 days ago, it's probably finished." If **today's** stock (or the sales forecast) says it is running low, the wording changes: "only 3 left today, top up before it runs out". Never pitches a product that is out of stock. |
| **Unpaid orders & overdue balances** | Opted-in customers who started a chat order and never paid (only while stock can still fill it), and customers with a balance past its due date | Reminds them; for the order, "reply YES for a fresh payment account" |
| **Offers from stock** | Opted-in customers, matched to a product in the same category as their last purchase that is in stock and worth moving (plenty of cover, or no recent demand) | Names what they bought last and the new product at its real price. Never invents a discount. |
| **Restock alerts to the admin** | You (`Notify__AdminPhone`, `Notify__AdminEmail`) | "Good morning Ada! Stock check from today's inventory: Adult Dog Food 20kg: 6 bags left, about 1.2 a day, out around 26 Oct. Suggested order: 40. Chukwu is your biggest buyer." Only on days something is within the warning window (default 7 days). |

**How a customer's reply works.** A reminder is not a dead end. When it goes out, that customer's chat is primed: replying **YES** prepares the same order and shows the bank account;
replying with a number to an offer sets the quantity. The sale then completes inside the chat exactly as before.

**Why no per-message approval, and what keeps it safe.**
* The wording is drafted by the AI **once per situation and language per run** (a few variants, not one call per customer; `Campaigns__MaxAiCallsPerRun`, default 15), with placeholders. Code fills
  the placeholders from the database. Any draft that contains a digit, a currency symbol, a link, an unknown field, or no `{name}` is thrown away and the built-in wording is used.
  So messages differ day to day and person to person, but the AI cannot state a price, a quantity or a stock level.
* Only customers with an explicit **yes** (asked once, in their own language, before their first order summary) are messaged. `STOP` or `UNSUBSCRIBE` in the chat, or the link in any email, ends it at once.
  A bare `STOP` in the middle of an order still means "cancel the order". `SUBSCRIBE` turns it back on.
* One marketing message per customer per cooldown (default 3 days, across all campaigns), a per-run cap, never before 07:00 or after 20:30 Lagos time, and a campaign that was missed by more than 3 hours
  (server down) skips the day instead of texting at lunchtime.
* Every message and every refusal (with the reason) is listed under **What was sent**.

## Extra things I need for campaigns

1. **Approved WhatsApp templates** (Twilio Console → Messaging → Content Template Builder → submit for WhatsApp approval). WhatsApp only lets a business start a conversation with an approved template; free text works only within
   23 hours of the customer's last message. The freshly written text travels in a template variable. Create one template per campaign (or reuse one) with this shape:
   `Hi {{1}}, a note from {{3}}: {{2}}` — category **Utility** for order/restock reminders, **Marketing** for offers; variables: 1 = first name, 2 = the message, 3 = shop name. Meta rejects templates that are only variables, so keep the words around them.
   Then set `Twilio__Templates__Reorder=HX...`, `__UnpaidOrder`, `__Promo`, `__AdminRestock`. Without a template, customers who haven't written to the bot in the last 23 hours are skipped (and the log says so).
   For your own restock alert: message the bot number once a day, or use the template.
2. **A real WhatsApp sender** (not the sandbox) for production marketing, and the **Meta business verification** that comes with it.
3. **SendGrid** for email: `SendGrid__ApiKey=SG...` (Mail Send permission) and `SendGrid__FromEmail=` a verified sender or an address on an authenticated domain. Without them, campaign email falls back to the Zoho mailbox.
4. `Campaigns__SecretKey=` (any long random string; signs unsubscribe links — falls back to the Twilio auth token) and `Campaigns__PublicBaseUrl=https://your-domain` (falls back to `Twilio__PublicBaseUrl`).
   Email campaigns are skipped without these, because every email must carry a working unsubscribe link.
5. Optional: `Campaigns__AdminName=Ada Obi` so the alert greets you by name.
6. Email only reaches customers whose record has an email address (the chat doesn't ask for one yet).
