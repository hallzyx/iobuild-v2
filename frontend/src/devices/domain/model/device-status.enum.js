/**
 * Device status taxonomy — canonical values used across domain and presentation.
 * Online statuses are normalized via isOnlineStatus helper (ADR-OD-1).
 */

export const DeviceStatus = Object.freeze({
    ONLINE: 'Online',
    OFFLINE: 'Offline',
    ACTIVE: 'active',
});

export const ONLINE_STATUSES = Object.freeze(['online', 'active']);

/**
 * Returns true if status is an online-equivalent value (case-insensitive, trimmed).
 * @param {string|null|undefined} status
 */
export function isOnlineStatus(status) {
    return ONLINE_STATUSES.includes(String(status ?? '').trim().toLowerCase());
}
