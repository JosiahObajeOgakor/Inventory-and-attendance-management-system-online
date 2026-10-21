# Go-live checklist — WhatsApp sales chat, payments, campaigns

Nothing here has been deployed. Everything below was verified locally against the real Twilio-format signatures, the real AlatPay API and the real OpenAI API (see "What was verified"); the steps are what remains to make it live.

## 1. Server (once)
Follow `deploy/README.md` (NGINX + Kestrel + MySQL on loopback, HTTPS via certbot). Note the runbook has never been run on a real machine, so do the first run as a rehearsal. `deploy/scripts/deploy.sh` builds for **linux-arm64**; for an x86 server change `-r linux-arm64` to `-r linux-x64`.

## 2. Server settings (`/etc/inventory/api.env`, root-owned, mode 600)
Start from `src/Inventory.Api/.env.example`. The values that matter for this feature:

| Setting | Value |
|---|---|
| `Companies` | already in `appsettings.json`: keys are **`chewypets`** and `candid`. |
| `Twilio__Numbers__0__CompanyKey` | **`chewypets`** (not `chewypetsfeeds`: the wrong key makes the bot ignore every message; the API now logs an ERROR at start-up if it is wrong) |
| `Twilio__Numbers__0__From` | the approved WhatsApp sender, e.g. `+2347054272549` |
| `Twilio__AccountSid`, `Twilio__AuthToken` | from the Twilio console |
| `Twilio__PublicBaseUrl` | `https://<your-domain>` exactly as Twilio will call it (no trailing slash). The signature covers this URL. |
| `AlatPay__SubscriptionKey`, `__BusinessId`, `__WebhookSecret` | from the AlatPay dashboard. Use **live** keys on the server (the ones you tested with may be sandbox). |
| `OpenAI__ApiKey` (+ `OpenAI__Model`, default `gpt-4.1-nano`) | optional; without it the bot is fully deterministic |
| `Notify__AdminEmail`, `Notify__AdminPhone`, `Smtp__*` | so you are told about every payment, "paid but not fulfilled", and get the morning restock digest |
| `Campaigns__SecretKey`, `Campaigns__PublicBaseUrl` | for email unsubscribe links (falls back to the Twilio token / base URL) |
| `Twilio__Templates__Reorder / UnpaidOrder / Promo / AdminRestock` | approved WhatsApp template SIDs (`HX…`); see `docs/WHATSAPP_SALES_SETUP.md` |
| `SendGrid__ApiKey`, `SendGrid__FromEmail` | optional, for campaign email |

Never reuse the dev login (`admin / ChangeMe1234`). Create the real first admin with the one-time `Setup__Token`, then remove it from the file.

## 3. Consoles
* **Twilio** → your WhatsApp sender → "When a message comes in": `https://<domain>/api/twilio/whatsapp` (POST).
* **AlatPay** dashboard → webhook URL: `https://<domain>/api/alatpay/webhook`, enable webhook retries, copy the webhook secret into `AlatPay__WebhookSecret`.
* AlatPay calls from `74.178.162.156`; if you want a second layer, allow only that address on `/api/alatpay/webhook` in NGINX (the signature check is already mandatory).

## 4. Shop data the bot trusts
* ChewyPets: `DefaultWarehouseId` is `1` — it must be the warehouse sales come out of. Candid Purrfect is `0`, so the chat will not sell there until it is set.
* Products active, retail price set, stock batches in that warehouse. Serial-tracked products are not offered in chat.
* **Company & documents** → list your real bank accounts (up to 3). These are the accounts offered when recording a bank-transfer payment (sales, customer payments) and a supplier payment.

## 5. After deploying, check in this order
1. `https://<domain>/api/health` → `healthy`; the API log has no `Startup` ERROR lines.
2. From your own phone: `hello` → order a product → answer the permission question → `yes` → you receive a Wema account number in the chat.
3. Pay a small real amount to it → the confirmation arrives in the same chat; the invoice shows in **Sales** with "received into AlatPay account …"; stock dropped by exactly the quantity.
4. Send `STOP` → "You won't get reminders…"; `SUBSCRIBE` → back on.
5. **Campaigns** screen → switch one on, press "Show me today's messages", then "Run now" with one opted-in customer (yourself).
6. Record a bank-transfer payment from **Customers → Record payment**: the account list shows, and the sale detail lists who paid and into which account.

## 5b. K9 Cravings (third business, for demos)
* It ships in `appsettings.json` (key `k9cravings`, listed after Candid Purrfect and ChewyPets). On the first start after deploying, the API creates and migrates its own empty database `inventory_k9cravings`; ChewyPets' and Candid's data are not touched or copied.
* Existing users can't open it until you allow them: **People & access** → edit the user → tick K9 Cravings.
* Then, in K9 Cravings: add a warehouse (the chat sells from warehouse id 1, i.e. the first one you create), categories, products and its bank accounts (Company & documents).

## 6. Rollback
`sudo systemctl stop inventory-api` stops the bot (customers simply get no reply; Twilio and AlatPay retry or fail visibly). Switch campaigns off on the Campaigns screen to stop outgoing messages without stopping the app. EF migrations are additive (new columns/tables) and do not need undoing to roll the code back.

## What was verified before this checklist
* 103 unit tests and the integration suite on a real MySQL 8 (sales, payments, stock reservation, the full WhatsApp conversation in English/Pidgin/Igbo/Hausa, opt-in, campaigns, payment accounts).
* Live, against the running API: a Twilio-signed message gets a stock-backed reply; a bad signature gets 401; with the real OpenAI key a slang order ("that big bag wey dog dey chop, two of dem") became *2 × Adult Dog Food 20kg* at the database price using **1 AI call**; the real AlatPay API issued a Wema account for the order; "I don pay" asked AlatPay and honestly answered "never see"; a correctly-signed **forged** "completed" webhook changed nothing because AlatPay itself did not confirm; a Reorder preview was drafted by OpenAI from live stock and the customer's own purchase.

## Still open (needs a person)
* **Igala**: no wording written. Customers writing Igala get Pidgin replies. Add the strings via `SalesAssistant__PhrasesFile` (format in `docs/WHATSAPP_SALES_SETUP.md`).
* Approved WhatsApp templates (needed to message customers who have not written to the bot in the last 23 hours).
* A real money test end to end (a live payment into an AlatPay virtual account) — only possible once the webhooks can reach a public address.
