-- ApprovalPO: confirm SY_USER approval UDFs on the tenant DB used by TNT10003
-- (the Firebird file from tenant config, e.g. ACC-UAMAT.FDB — not a different test DB).
--
-- Columns:
--   UDF_APPROLE   MANAGER | HOD | PUR | DIRECTOR
--   UDF_APPLEVEL  1 | 2 | 3
--   UDF_APPFROM / UDF_APPTO  inclusive personal amount range (RM)
--
-- Jason (example): range 0–20000 cannot approve RM 20,100; level 1 on a 2-level PO
-- stays Waiting (1/2) only when the amount is inside his range.

SELECT TRIM(RDB$FIELD_NAME) AS COL_NAME
FROM RDB$RELATION_FIELDS
WHERE TRIM(RDB$RELATION_NAME) = 'SY_USER'
  AND TRIM(RDB$FIELD_NAME) STARTING WITH 'UDF_APP';

SELECT
  TRIM(EMAIL) AS EMAIL,
  TRIM(UDF_APPROLE) AS UDF_APPROLE,
  UDF_APPLEVEL,
  UDF_APPFROM,
  UDF_APPTO
FROM SY_USER
WHERE UPPER(TRIM(CAST(EMAIL AS VARCHAR(320)))) = 'JASON.CHOO2004@GMAIL.COM';
