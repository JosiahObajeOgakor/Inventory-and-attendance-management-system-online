# Sales assistant chat API

A single stateless endpoint that talks to the same AI sales assistant used by the WhatsApp number and the
landing-page chat widget — product Q&A, quotations, checkout links, order status, and pet nutrition guidance.
There's no separate OpenAPI/Swagger surface in this project; this page is the documentation.

## `POST /api/webchat/message`

No authentication. Company is fixed to `chewypets`.

**Rate limit:** 20 requests/minute per IP (HTTP 429 past that), plus a 60-messages/24h limit per conversation
enforced server-side regardless of channel.

### Request

```json
{ "sessionId": "<any string you generate and reuse, up to 100 chars>", "message": "Do you sell puppy food?" }
```

`sessionId` is how the assistant recognizes it's the same conversation on your next call — generate one
per user session (e.g. a UUID) and send the same value on every message. A new `sessionId` starts a fresh
conversation with no memory of any previous one.

### Response

```json
{ "reply": "Yes! ...", "checkoutUrl": null, "quotationNumber": null }
```

- `reply` — the assistant's message, in whatever language you wrote in.
- `checkoutUrl` — present once a quotation has a Paystack payment link; open it to pay. The order is only
  confirmed, and stock only deducted, once payment completes.
- `quotationNumber` — present once a quotation has been created in this conversation.

### Example

```bash
curl -s https://chewypetfeeds.com/api/webchat/message \
  -H 'Content-Type: application/json' \
  -d '{"sessionId":"demo-1","message":"I need 2 bags of the all-life-stages formula"}'
```

### Errors

Standard `ProblemDetails` JSON on failure (400 for a missing/invalid `sessionId` or an empty message, 429 for
the rate limit, 409 for a business rule like "no warehouse configured for online sales").
