# StockDesk licensing — how it works

**$250, one payment, lifetime, per computer.** Fully automatic — you are never in
the loop to issue a key.

## The pieces

| | Where | Holds |
|---|---|---|
| **Desktop app** | customer PC | the **public** signing key only — can *verify* a licence offline forever, can never *forge* one |
| **`licensing-api/`** | Render (free) | the **private** signing key, the AlatPay secret key, and a Turso DB of orders + licences |
| **Turso** | turso.tech (free) | `orders`, `licenses`, `transfers` — the only database anywhere |

## Payment rail

AlatPay **Bank Transfer (virtual account)** — NGN only. `POST /api/paylink`
generates a one-time Wema Bank account for exactly this payment; the customer
transfers to it; AlatPay confirms by webhook (or the app polls). No hosted page,
no card. `NGN_FIXED_PRICE` sets the amount. Handle non-Nigerian buyers before
public launch (needs the payment-links rail + a *secret* API key, or a USD
virtual account).

## Customer flow

1. First run → **Activate** screen shows the **Machine ID** + email.
2. **Get payment details** → app calls `POST /api/paylink` → shows a one-time
   **account number / bank / amount**.
3. Customer transfers the exact amount from any Nigerian bank app. AlatPay's
   webhook marks the order paid (the app also polls as a fallback).
4. **"I've paid — activate now"** → `GET /api/activate?machineId&email` → the API
   ECDSA-signs the machine id, stores the licence in Turso, returns the key. The
   app caches it in **the database and the registry**
   (`HKCU\SOFTWARE\SpringupAI\StockDesk`).
5. From then on the app verifies that key **offline** on every launch. No
   internet needed again. Reinstall on the same PC re-uses the cached key.
6. New PC → cached key doesn't verify → Activate screen → **transfer** (first
   auto-approved; later ones you approve at `/api/admin/overview`).

## Deploy (one-time, ~20 min — no domain needed)

The service lives at **`https://stockdesk-licensing.onrender.com`** (Render's
own free URL — no DNS). The app already points there (`LicenseApiUrl` in
`App.config`). If that Render name is taken, pick another, then update
`LicenseApiUrl` + `render.yaml`'s `name:` + `PUBLIC_BASE_URL`.

1. **Keypair**: `cd licensing-api && npm install && npm run keygen`
   → `EC_PRIVATE_KEY` goes in a Render env var; `PUBLIC_X`/`PUBLIC_Y` go into
   `Licensing.vb` (`PubKeyX` / `PubKeyY`) and you rebuild the app.
   *(A working pair is already committed — you can skip this and use it.)*
2. **Turso** (turso.tech): create an account + one database →
   `turso db show <name> --url` and `turso db tokens create <name>` →
   `TURSO_URL`, `TURSO_TOKEN`.
3. **Render**: New → **Web Service** → connect the repo (or "Deploy from a
   public Git URL") → root directory `licensing-api` → it reads `render.yaml`.
   Set the `sync:false` env vars in the dashboard:
   `EC_PRIVATE_KEY`, `ADMIN_TOKEN` (long random), `TURSO_URL`, `TURSO_TOKEN`,
   `ALATPAY_SECRET_KEY`, `ALATPAY_WEBHOOK_SECRET`.
4. **AlatPay dashboard**: set the webhook URL to
   `https://stockdesk-licensing.onrender.com/api/alatpay/webhook` and copy its
   secret into `ALATPAY_WEBHOOK_SECRET`.
5. Test: open `https://stockdesk-licensing.onrender.com/api/health` → `{ ok: true }`.

**Render free note:** the service sleeps after 15 min idle and takes ~40s to
wake. That's fine — if AlatPay's webhook hits a sleeping service it retries
(30 min / 1 hr / 24 hr), and when the customer clicks "I've paid — activate now"
the app both wakes the service *and* triggers a direct status check with AlatPay.
So the customer's own click is the reliable confirmation path; the webhook is the
fast path when the service is already awake.

## Rotating the keypair

`npm run keygen` again → new `EC_PRIVATE_KEY` on Render + new `PubKeyX`/`PubKeyY`
in the app → rebuild + redeploy. **All existing licences stop verifying** and
customers must re-activate (the API re-issues from the paid orders in Turso, so
it's automatic for them).

## Installing for a customer yourself (no payment)

Two vendor-only options, both entered under **"Have an installation code or offline key?"**
on the activation screen:

- **Offline key (no internet needed)** — copy the customer's 16-character Machine ID,
  then on your laptop in `licensing-api/`: `npm run offlinekey -- <MACHINE-ID> "customer name"`.
  Paste the printed key into their PC. It only works on that one PC, so a leaked key is
  useless elsewhere. Needs `EC_PRIVATE_KEY` in your local `.env`; every key issued is
  logged to `licensing-api/offline-keys.log`.
- **One-time code (their PC needs internet once)** — `npm run gencode -- "customer name"`
  prints a 12-character code. The customer enters their email + the code; the server burns
  it on first use.

## Support switches

- `App.config` → `SkipActivation = true` unlocks the app with no licence — **Debug builds
  only**; Release builds ignore it, so customers can't use it.
- `App.config` → `LicenseApiUrl` points the app at a staging API.
- `StockDesk.exe --machineid` prints a PC's Machine ID without opening the GUI.
- `StockDesk.exe --activate <email>` runs activation from the command line.
- `POST /api/admin/order {email, paid:true}` (Bearer `ADMIN_TOKEN`) records a
  manual/off-AlatPay sale (bank transfer, comp) so that email can activate.
