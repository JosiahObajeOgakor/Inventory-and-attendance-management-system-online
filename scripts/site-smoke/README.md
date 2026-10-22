# Website smoke test: the shop's checkout, clicked in a real browser

Run this before every website release. It opens the shop in Microsoft Edge, adds a product, opens **Pay online**, fills the form and presses **Pay**.

```
npm install playwright-core@1.48.2
# a new build, before deploying (serves it locally, data from the live API; nothing is created):
node serve.js <path to dist/landing-ui/browser> 4300
node checkout.js <screenshot folder> intercept http://localhost:4300 1366
node checkout.js <screenshot folder> intercept http://localhost:4300 390      # phone width
# the live site, all the way to AlatPay's page (creates one unpaid quotation for "Website test - please ignore"):
node checkout.js <screenshot folder> live https://chewypetsfeeds.com 1366
```

It must print `CHECKOUT SENT` (intercept) or end on `paylink.alatpay.ng` (live). If it doesn't, don't ship.