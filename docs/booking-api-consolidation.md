# Booking API consolidation

## Decisions applied

- Keep owner-first booking approval/rejection through `BookingRepository.Decisions.cs`: transaction, vehicle lock, ownership/role checks, conflict checks and idempotent replay. Remove the older repository methods that only updated status.
- Keep VNPay checkout and verified IPN processing. Payment amounts come from the saved booking quote, not a client amount or the current vehicle price. Browser return is not proof of payment.
- Remove `POST /api/bookings/{id}/confirm-payment` and its service/repository/DTO. It could mark a booking paid without a verified payment. Clients must use `POST /api/payments` with an `Idempotency-Key` instead.
- Share rental pricing between catalog estimates and booking quotes. Keep database creation authoritative for the persisted snapshot; regression tests compare all three and verify later vehicle price changes do not rewrite the booking price.
- Share mileage calculation between return and fee reads. `GET /api/bookings/{id}/excess-mileage` no longer saves or updates charges. The return command persists the charge; GET returns its existing ID.

## Catalog regression

Nullable SQL search parameters have explicit PostgreSQL types. Empty filters remain supported; incomplete rental windows and invalid filters return validation errors before executing SQL. Unexpected API exceptions return generic ProblemDetails with a trace ID, including CORS headers for allowed origins only.

Tests use a newly generated local database with structural migrations, never the application's deployed connection or demo seed data. The local fixture accepts PostgreSQL 16+; CI runs PostgreSQL 16. Shared/payment-staging tests remain explicitly opt-in.

## Deployment

The existing deployment workflow now gates image publication on tests and checks the catalog endpoint after deployment, in addition to `/health`. The self-hosted runner needs `curl` and `jq`; prerequisite checks happen before replacing the API container. `API_CATALOG_URL` can override the default `http://127.0.0.1:8080/api/vehicles`.

No production database migration is required for these changes. Deployment and automatic rollback were not performed by this code change.

## Optional payment sample

`database/seeds/payment_sample.sql` is an operator-run, transactional, idempotent seed, not a startup migration. It creates only vehicle `90000000-0000-0000-0000-000000005000`, plate `BONBON-PAYMENT-TEST`, with a clear test-only name/description, 5,000 VND/day (and hour), no weekly discount or mileage charge. It reuses approved Vios imagery and its existing active owner's ID; no original vehicles, prices, roles, accounts or bookings are modified. A conflicting vehicle ID, unavailable source, inactive/non-owner account, missing images or unintended database causes a rollback.

The seed is tested twice against a disposable PostgreSQL database to verify replay safety, shared image URLs and unchanged original vehicle/booking prices. Creating the sample does not enable payments or install the separate payment-schema proposals. Merchant credentials remain server-only; never add them to this repository.
