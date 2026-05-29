OCR captures
============

This folder holds images captured via the "OCR scan" button on the
ScanPO detail page (/ScanPODetail). Files are written by
ApprovalPO.Services.Ocr.OcrCaptureService.

Naming: {PO}_{yyyyMMdd-HHmmss}_{random}.{png|jpg|jpeg|webp}
A matching .txt sidecar holds the recognized OCR text (when any was read).

Everything here is OCR-related. It is safe to clear old files periodically.
