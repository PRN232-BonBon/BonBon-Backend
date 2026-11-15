-- READ ONLY. Run as restricted staging runtime role, never against production.
-- psql -X -v ON_ERROR_STOP=1 -v expected_marker='<verified marker>' -v expected_db_address='<private container IP>' -f ...
-- No defaults: missing operator-supplied identity values must fail.
\set ON_ERROR_STOP on
BEGIN READ ONLY;
SET LOCAL statement_timeout='10s';
SELECT current_database()='bonbon_payment_staging'
  AND current_user='bonbon_payment_staging_runner' AND session_user=current_user
  AND pg_get_userbyid(d.datdba)='bonbon_payment_staging_owner'
  AND shobj_description(d.oid,'pg_database')=:'expected_marker'
  AND inet_server_addr()=:'expected_db_address'::inet AND inet_server_port()=5432
  AND current_setting('server_version_num')::int=160015
  AND NOT r.rolsuper AND NOT r.rolcreatedb AND NOT r.rolcreaterole AND NOT r.rolbypassrls
  AND NOT r.rolreplication AND r.rolconnlimit=4
  AND NOT has_database_privilege(current_user,current_database(),'CREATE')
  AND NOT has_schema_privilege(current_user,'bonbon','CREATE')
  AND NOT has_schema_privilege(current_user,'public','CREATE')
  AND NOT EXISTS(SELECT 1 FROM pg_roles other WHERE other.rolname<>current_user AND pg_has_role(current_user,other.oid,'MEMBER'))
  AND NOT EXISTS(SELECT 1 FROM pg_database other WHERE NOT other.datistemplate AND other.datname NOT IN('postgres','bonbon_payment_staging'))
  AND NOT has_database_privilege(current_user,'postgres','CONNECT')
  AS stage_identity_ok
FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database()
\gset
\if :stage_identity_ok
\echo 'Dedicated staging database identity PASS'
\else
\echo 'STOP: unexpected database/server/role/marker/privileges'
\quit 3
\endif

SELECT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1)
 AND EXISTS(SELECT 1 FROM bonbon.momo_gateway_schema WHERE version=1)
 AND EXISTS(SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1)
 AND EXISTS(SELECT 1 FROM public.payment_staging_control WHERE singleton=1 AND stage='ready')
 AND EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='bonbon'
       AND p.proname='protect_booking_quote' AND p.prosrc LIKE '%NEW.customer_account_id IS DISTINCT%'
       AND p.prosrc LIKE '%discount_amount_snapshot%' AND p.prosrc LIKE '%NEW.status <> ''PendingApproval''%')
 AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid='bonbon.booking'::regclass AND tgname='booking_quote_guard'
       AND NOT tgisinternal AND tgenabled IN('O','A') AND tgfoid='bonbon.protect_booking_quote()'::regprocedure)
 AND (SELECT count(*)=5 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='booking'
       AND column_name IN('weekly_discount_percent_snapshot','discount_amount_snapshot','included_mileage_km_snapshot','overage_fee_per_km_snapshot','rental_terms_snapshot'))
 AND (SELECT count(*)=12 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
       WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready AND c.relname IN
       ('uq_vnpay_sandbox_active','uq_vnpay_sandbox_idempotency','uq_vnpay_sandbox_accepted_success','uq_vnpay_sandbox_transaction',
        'uq_momo_sandbox_active','uq_momo_sandbox_idempotency','uq_momo_sandbox_accepted_success','uq_momo_sandbox_transaction',
        'uq_sepay_sandbox_active','uq_sepay_sandbox_idempotency','uq_sepay_sandbox_accepted_success','uq_sepay_sandbox_transaction'))
 AND (SELECT count(*)=2 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid JOIN pg_namespace n ON n.oid=c.relnamespace
       WHERE n.nspname='bonbon' AND i.indisunique AND i.indisvalid AND i.indisready
       AND i.indnkeyatts=1 AND pg_get_indexdef(i.indexrelid,1,true)='booking_id'
       AND c.relname IN('uq_sandbox_active_booking','uq_sandbox_accepted_booking')
       AND pg_get_expr(i.indpred,i.indrelid) LIKE '%SEPAY_SANDBOX%'
       AND pg_get_expr(i.indpred,i.indrelid) LIKE '%VNPAY_SANDBOX%'
       AND pg_get_expr(i.indpred,i.indrelid) LIKE '%MOMO_SANDBOX%'
       AND ((c.relname='uq_sandbox_active_booking' AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Pending%'
               AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Processing%')
         OR (c.relname='uq_sandbox_accepted_booking' AND pg_get_expr(i.indpred,i.indrelid) LIKE '%Succeeded%'
               AND pg_get_expr(i.indpred,i.indrelid) LIKE '%NOT needs_reconciliation%')))
 AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.payment'::regclass AND conname='ck_sepay_sandbox_attempt' AND convalidated)
 AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid='bonbon.sepay_payment_evidence'::regclass AND confrelid='bonbon.payment_notification'::regclass AND contype='f' AND convalidated)
 AND EXISTS(SELECT 1 FROM information_schema.columns WHERE table_schema='bonbon' AND table_name='payment'
       AND column_name='provider_transaction_no' AND data_type='character varying' AND character_maximum_length=100)
 AS stage_schema_ok
\gset
\if :stage_schema_ok
\echo '004/005/006/007 markers, quote guard, indexes, UUID storage and audit FK PASS'
\else
\echo 'STOP: staging schema/prerequisite drift; do not enable SePay'
\quit 4
\endif
COMMIT;
