/**
 * Centralized application constants — avoids magic numbers scattered across stores and views.
 * Values that are deployment-specific are overridable via VITE_* env vars.
 */

export const POLLING_INTERVAL_MS = Number(import.meta.env.VITE_POLLING_INTERVAL_MS) || 30000;

export const ANALYTICS_DEFAULT_MINUTES = Number(import.meta.env.VITE_ANALYTICS_DEFAULT_MINUTES) || 10;

export const RETRY_DELAY_SHORT_MS = 1500;

export const RETRY_DELAY_LONG_MS = 2500;

export const RETRY_DEVICE_CONFIRM_INTERVAL_MS = 1000;

export const RETRY_DEVICE_CONFIRM_ATTEMPTS = 12;

export const RETRY_OWNER_DASHBOARD_MAX_ATTEMPTS = 4;

export const TOAST_SUCCESS_DURATION_MS = 2500;

export const TOAST_ERROR_DURATION_MS = 3000;

export const TOAST_INVOICE_ERROR_DURATION_MS = 4000;

export const TOAST_AUTH_ERROR_DURATION_MS = 5000;

export const CLOUDINARY_WIDGET_URL =
    import.meta.env.VITE_CLOUDINARY_WIDGET_URL || 'https://widget.cloudinary.com/v2.0/global/all.js';

export const APP_URL = import.meta.env.VITE_APP_URL || window.location.origin;
