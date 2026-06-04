-- ApprovalPO: purchase order approval tabs (Pending / Approved / Cancelled / Rejected).
-- Run once per tenant database (FlameRobin / isql) if Scan PO or Purchase Orders fails without UDF_POSTATUS.
--
-- If the column already exists, Firebird returns an error — safe to ignore.

ALTER TABLE PH_PO ADD UDF_POSTATUS VARCHAR(40);

-- Optional: mark existing POs as pending
-- UPDATE PH_PO SET UDF_POSTATUS = 'PENDING' WHERE UDF_POSTATUS IS NULL OR TRIM(UDF_POSTATUS) = '';
