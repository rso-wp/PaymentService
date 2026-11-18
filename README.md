# PaymentService

Everything about **money and Stripe** for RSO (a WordPress-as-a-service platform): the customer's Stripe account, saved cards,
subscriptions, and what happens when a payment fails.

```
web app --REST + bearer token--> PaymentService (:5264) --> Stripe
ProvisioningService --gRPC (:5263)--> PaymentService       (start / resolve / stop a subscription)
Stripe --webhook--> PaymentService                        (payment failed, subscription ended)
UserService --UserCreated (NATS)--> PaymentService         (creates the Stripe customer)
PaymentService --SubscriptionEnded (NATS)--> ProvisioningService   (shuts the site down)
PostgreSQL (PaymentServiceDb): Customers (EF Core) and the event-sourced Subscription (Marten)
```

How it fits with the other services, and how to run all of it on your machine or on Azure: the **Deployment** repository.

## What it does

- **Customers:** a Stripe customer per user, created when UserService announces a new user (or on demand when a card is added or
  a site is bought).
- **Cards (REST, needs a Keycloak bearer token):**
  - `POST /payment-methods/setup-intent`: a Stripe SetupIntent; the web app saves the card with Stripe Elements.
  - `GET /payment-methods`: the user's saved cards.
  - `GET /payment-methods/config`: `stripe` with the publishable key, or `fake` in local development.
  - `POST /payment-methods/fake`: adds a fake card (only with `Stripe:UseFake`).
- **Subscriptions (gRPC on port 5263, used by ProvisioningService only):**
  - `TryMakeSubscription`: starts one. The caller's request id is the subscription id, so a repeated call never charges twice.
  - `ResolveSubscription`: finds out what became of a call whose answer was lost.
  - `StopSubscription`: cancels it.
  Every step is recorded as an event on the subscription's Marten stream (`Pending`, `Active`, `FailedToStart`, `Canceled`).
- **Stripe webhooks:** `POST /stripe/webhooks` (no user auth; the `Stripe-Signature` is verified). It acknowledges at once and
  handles the event from a durable queue. The first failed payment ends the subscription, cancels it in Stripe and publishes
  `SubscriptionEnded`.
- **Safety net:** every 6 hours each `Active` subscription is checked against Stripe, in case a webhook was missed.
- **Health and metrics:** `/health/live`, `/health/ready`, `/metrics`, `/swagger`.

## Run it locally

You need the .NET 10 SDK and Docker. The infrastructure (PostgreSQL, NATS, Keycloak) comes from the compose file in the
**Deployment** repository, which must sit next to this one:

```powershell
docker compose -f ..\Deployment\local\docker-compose.yml up -d     # PostgreSQL :5432, NATS :4222, Keycloak :8180
dotnet run --project PaymentService                                  # gRPC :5263, REST :5264
```

In Development `Stripe:UseFake` is `true`: cards are fake and no Stripe account is needed (a card ending in `0002` is
declined, to try the failure path). Fake webhooks can be sent with `Deployment\local\send-stripe-event.py`. To use real Stripe
test mode, set `Stripe:UseFake` to `false` and give it your test keys.

## Configuration

Settings come from `appsettings*.json` and environment variables (`__` for nesting).

| Setting | Meaning |
|---|---|
| `ConnectionStrings__DefaultConnection` | PostgreSQL, database `PaymentServiceDb` (also holds Marten's events) |
| `Nats__Url` | NATS server |
| `Auth__Issuer`, `Auth__Audience`, `Auth__MetadataAddress` | Keycloak: the token's `iss`, `rso-api`, and the discovery document the signing keys come from |
| `Stripe__SecretKey`, `Stripe__PublishableKey` | Stripe test (or live) keys |
| `Stripe__WebhookSecret` | `whsec_...` of the webhook endpoint |
| `Stripe__UseFake` | `true`: no Stripe at all (development only) |
| `Cors__AllowedOrigins` | origins of the web app |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | optional: also send the logs to Application Insights |

## Tests

```powershell
dotnet test Test/Test.csproj
```

The gRPC methods are tested against mocked Stripe and Marten (which Stripe calls and events each method makes). The paths that
look a subscription up through Marten's query need a real PostgreSQL and are not covered.

## Container image and release

`PaymentService/Dockerfile` builds the image (build from this folder: `docker build -f PaymentService/Dockerfile .`; ports 5263
and 5264). **Push a git tag like `v1.2.3`** and the *Publish image* workflow pushes `armodilo/paymentservice:v1.2.3` to Docker
Hub (it needs the repository secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`); Argo CD Image Updater then deploys it.
Pushes and pull requests run the tests (`.github/workflows/ci.yml`).
