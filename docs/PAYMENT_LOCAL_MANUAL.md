# Isolated Windows manual checkout — 15/11/2026

Running on review/payment-sandbox-web. No commit/push, production deployment,
shared database connection, shared SQL, firewall, PATH, Windows service or OS
certificate trust changes were made in this preparation.

## Runtime

- API http://127.0.0.1:5288, environment PaymentLocal, Payment enabled locally.
- UI http://127.0.0.1:5289 with vite.payment-local.config.ts (API proxy local only).
- PostgreSQL16.15 portable: BonBon.API/bin/payment-local-postgres/pgsql.
- Cluster data: BonBon.API/bin/payment-local-postgres/data, listening127.0.0.1:55432.
- Database bonbon_payment_local; runtime bonbon_payment_local_runner is not superuser,
  cannot create roles/databases, connection limit4/pool2. Admin credential remains local.
- No Windows service or automatic startup was registered. PostgreSQL stays running
  for manual testing, but may need starting again after Windows reboots.

All private files and binaries/data/logs are Git ignored; do not copy them into commits:

- BonBon.API/.payment-local.local.json — local DB/JWT/sandbox gateway configuration.
- BonBon.API/.payment-local-db.local.json — private cluster admin/runtime passwords.
- BonBon.API/.payment-local-accounts.local.json — synthetic Customer/Owner passwords
  and IDs (AwaitingPaymentBookingId, PendingBookingId, ProbeBookingId/ProbePaymentId).
- TLS PFX and provisioning scratch files live under the ignored bin directory.

Original shared-test config/accounts were retained unchanged, not used by PaymentLocal.
Old local test-group sessions in browser storage may fail JWT validation; sign out
and sign in with the NEW .payment-local-accounts.local.json credentials.

## Schema created only on the new local database

After checking host127.0.0.1, port55432 and database identity:
001_initial_schema;003_max_daily_rental;003_vehicle_tracking;
004_minimal_vehicle_details;006_owner_vehicle_registration;
007_remove_vehicle_registration_document;008_admin_vehicle_review;
then proposals004_vnpay_sandbox and005_booking_quote_guard.

Historical002 expects an old quantity column absent from001; skipped. Demo005 seed
skipped. Both schema004 and Payment proposal004 are required, despite similar numbers.
Do not rerun the one-shot baseline/proposal on the existing local database.

PaymentLocal validates fixed loopback connection/name/role and DB comment marker
bonbon-payment-local:v1 for every physical connection; startup/health require Payment
marker, indexes and new quote snapshot fields/guard. It does not weaken PaymentStaging
or VnPayOptions. Listeners and frontend return are fixed to local addresses.

## Manual operation

1. Open the ignored accounts JSON locally. Use CustomerEmail/CustomerPassword to log in
   at http://127.0.0.1:5289, then /my-trips. Never paste passwords or signed URLs into chat.
2. AwaitingPaymentBookingId is the two-day future booking for YOUR first checkout,
   price1,300,000VND; initially no attempt. Click Thanh toán, then use the VNPay link.
3. PendingBookingId is another future, non-overlapping request. Sign out/log in with
   OwnerEmail/OwnerPassword, open /owner/bookings and approve it. Then return as Customer.
4. You may create additional requests from /cars. Pick dates that do not overlap
   accepted bookings. Vehicle is BonBon Synthetic Local Car, plate LOCAL-PAY-01.
5. A separate third booking was used for automated local checkout verification;
   do not choose ProbeBookingId for a fresh checkout (it already has a Pending attempt).
6. Do not clear storage/change keys to retry an uncertain payment; no automatic
   new attempt after expiration/failure. Stop for reconciliation if necessary.

Verified real local Auth/Booking/Approval/Payment APIs, a checkout URL on the exact
sandbox gateway, authenticated status Pending, and same-key replay/same Payment ID.
Signing a URL does NOT contact VNPay or prove merchant acceptance. No gateway request,
financial transaction, fake success callback or Payment Succeeded was performed.

## Callback limitation

Return is https://127.0.0.1:5287/api/payments/vnpay/return/redirect. This HTTPS listener
uses a self-signed certificate stored only in a local PFX, not added to Windows trust.
Do not bypass browser TLS warnings. Browser may warn if you reach that endpoint;
full Return testing needs a trusted local development certificate or approved HTTPS
staging. Merchant may reject a loopback Return URL. Do not treat checkout generation
as proof the gateway will accept it.

VNPay cannot call IPN on this private localhost. Do not complete a payment expecting
Confirmed: without verified server IPN/reconciliation it remains Pending/uncertain.
Return does not mutate payment state. PublicIpnReady=false is reported in /health.
QUERYDR/worker reconciliation/Booking TTL/refund remain unimplemented.

## Restart without changing unrelated services

Build Backend before starting it, then run tools/Start-PaymentLocal.ps1. If a local
port is busy, that script refuses to kill it. Vite command from Frontend:
`npm exec vite -- --config vite.payment-local.config.ts --mode payment-local`.
If the portable cluster is stopped, run its pg_ctl with the exact data directory;
do not register a service or reuse a shared connection. Do not publish these listeners.

Validation: Backend Release build passed;235 unit/API tests passed,0 failed,48 opt-in
tests skipped. Additional real local API preparation and checkout/replay/status probes
passed. These are local PostgreSQL checks, not VNPay E2E.
