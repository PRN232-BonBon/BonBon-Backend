-- BonBon: OTP đặt lại mật khẩu gửi qua email.
--
-- Chỉ lưu SHA-256 hash của OTP, không lưu mã gốc.
-- consumed_at NULL + chưa hết hạn = còn hiệu lực.
-- Mỗi email chỉ có tối đa một OTP hiệu lực tại một thời điểm
-- (API vô hiệu các OTP cũ trước khi tạo OTP mới).

BEGIN;

CREATE TABLE bonbon.password_reset_otp (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    email varchar(254) NOT NULL CHECK (btrim(email) <> ''),
    otp_hash varchar(512) NOT NULL CHECK (btrim(otp_hash) <> ''),
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    consumed_at timestamptz,
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    CONSTRAINT ck_password_reset_otp_consumed CHECK (consumed_at IS NULL OR consumed_at >= created_at)
);
CREATE INDEX ix_password_reset_otp_email_created
    ON bonbon.password_reset_otp (lower(btrim(email)), created_at DESC);

COMMENT ON TABLE bonbon.password_reset_otp IS
    'OTP đặt lại mật khẩu: chỉ lưu hash, một email một OTP hiệu lực tại một thời điểm.';

COMMIT;
