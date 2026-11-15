# Explicit desktop-only migration. Never accepts a remote database/connection string.
$ErrorActionPreference='Stop'
$backendRoot=Split-Path $PSScriptRoot -Parent
$branch=git -C $backendRoot branch --show-current
if($branch -ne 'review/payment-sandbox-web' -and $branch -notmatch '^feature/sepay-payment-integration(-[0-9]+)?$'){throw 'Unexpected branch.'}
$apiRoot=Join-Path $backendRoot 'BonBon.API'
$private=Get-Content -LiteralPath (Join-Path $apiRoot '.payment-local-db.local.json') -Raw | ConvertFrom-Json
$pgsqlBin=Join-Path $apiRoot 'bin/payment-local-postgres/pgsql/bin'
$env:PGHOST='127.0.0.1';$env:PGPORT='55432';$env:PGDATABASE='bonbon_payment_local';$env:PGUSER='postgres'
$env:PGPASSWORD=$private.AdminPassword;$env:PGCONNECT_TIMEOUT='5'
function Query([string]$sql){
 $result=& (Join-Path $pgsqlBin 'psql.exe') -X -q -t -A -v ON_ERROR_STOP=1 -c $sql 2>&1
 if($LASTEXITCODE -ne 0){throw 'Local SQL check failed; inspect privately. No credentials printed.'}
 return ($result -join "`n").Trim()
}
try {
 $identity=Query "SELECT current_database()='bonbon_payment_local' AND current_user='postgres' AND inet_server_addr()='127.0.0.1'::inet AND inet_server_port()=55432 AND shobj_description(oid,'pg_database')='bonbon-payment-local:v1' FROM pg_database WHERE datname=current_database()"
 if($identity -ne 't'){throw 'Local target identity rejected.'}
 if((Query "SELECT to_regclass('bonbon.sepay_gateway_schema') IS NULL") -ne 't'){throw 'SePay already present. No rerun or overwrite.'}
 if((Query "SELECT EXISTS(SELECT 1 FROM bonbon.payment_gateway_schema WHERE version=1) AND EXISTS(SELECT 1 FROM bonbon.momo_gateway_schema WHERE version=1) AND EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='bonbon' AND p.proname='protect_booking_quote' AND p.prosrc LIKE '%discount_amount_snapshot%')") -ne 't'){throw 'Baseline/quote guard drift.'}
 $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
 $backupRoot=Join-Path $apiRoot 'bin/payment-local-postgres/backups'
 New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
 $archive=Join-Path $backupRoot "pre-sepay-$stamp.dump"
 & (Join-Path $pgsqlBin 'pg_dump.exe') -Fc -f $archive 2>$null
 if($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $archive).Length -lt 1024){throw 'Backup failed.'}
 & (Join-Path $pgsqlBin 'pg_restore.exe') -l $archive | Out-Null
 if($LASTEXITCODE -ne 0){throw 'Backup archive invalid.'}
 $snapshotSql="SELECT jsonb_build_object('payments',(SELECT jsonb_agg(jsonb_build_object('id',payment_id,'hash',md5(to_jsonb(p)::text)) ORDER BY payment_id) FROM bonbon.payment p),'bookings',(SELECT jsonb_agg(jsonb_build_object('id',booking_id,'hash',md5(to_jsonb(b)::text)) ORDER BY booking_id) FROM bonbon.booking b),'audit',(SELECT md5(COALESCE(jsonb_agg(to_jsonb(a) ORDER BY payment_notification_id)::text,'')) FROM bonbon.payment_notification a))::text"
 $before=Query $snapshotSql
 $before | Set-Content -LiteralPath (Join-Path $apiRoot '.payment-sepay-migration.local.json') -Encoding UTF8
 $proposal=Join-Path $backendRoot 'database/proposals/007_sepay_sandbox.sql'
 $sql=Get-Content -LiteralPath $proposal -Raw
 # Inject a deterministic failure before COMMIT on this private local database only.
 $rollbackFile=Join-Path $backupRoot "sepay-rollback-$stamp.sql"
 $sql.Replace('COMMIT;','SELECT 1/0; COMMIT;') | Set-Content -LiteralPath $rollbackFile -Encoding UTF8
 $ErrorActionPreference='Continue'
 & (Join-Path $pgsqlBin 'psql.exe') -X -q -v ON_ERROR_STOP=1 -f $rollbackFile *> (Join-Path $backupRoot "sepay-rollback-$stamp.log")
 $rollbackExit=$LASTEXITCODE
 $ErrorActionPreference='Stop'
 if($rollbackExit -eq 0){throw 'Expected rollback failure did not occur.'}
 if((Query "SELECT to_regclass('bonbon.sepay_gateway_schema') IS NULL") -ne 't' -or (Query $snapshotSql) -ne $before){throw 'Rollback integrity failed.'}
 & (Join-Path $pgsqlBin 'psql.exe') -X -q -v ON_ERROR_STOP=1 -f $proposal *> (Join-Path $backupRoot "sepay-apply-$stamp.log")
 if($LASTEXITCODE -ne 0){throw 'SePay migration failed; transaction rolled back. Inspect local private migration log.'}
 if((Query $snapshotSql) -ne $before){throw 'Historical row digest changed.'}
 Query "GRANT SELECT ON bonbon.sepay_gateway_schema TO bonbon_payment_local_runner; GRANT SELECT,INSERT ON bonbon.sepay_payment_evidence,bonbon.sepay_rejected_notification TO bonbon_payment_local_runner" | Out-Null
 if((Query "SELECT EXISTS(SELECT 1 FROM bonbon.sepay_gateway_schema WHERE version=1) AND (SELECT count(*)=4 FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relname IN('uq_sepay_sandbox_active','uq_sepay_sandbox_idempotency','uq_sepay_sandbox_accepted_success','uq_sepay_sandbox_transaction') AND i.indisunique AND i.indisvalid AND i.indisready)") -ne 't'){throw 'SePay post-migration checks failed.'}
 Write-Output 'LOCAL ONLY: identity, baseline, backup archive, injected rollback, migration 007, historical row digests, indexes and runtime grants PASS.'
 Write-Output "Private backup: $archive"
} finally {Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue;Remove-Item Env:PGHOST,Env:PGPORT,Env:PGDATABASE,Env:PGUSER,Env:PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue}
