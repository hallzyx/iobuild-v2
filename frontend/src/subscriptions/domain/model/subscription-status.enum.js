/**
 * Subscription lifecycle statuses — mirrors backend enum (case-insensitive compare).
 */

export const SubscriptionStatus = Object.freeze({
    ACTIVE: 'active',
    CANCELLED: 'cancelled',
    EXPIRED: 'expired',
    PENDING: 'pending',
});

/**
 * Returns true if status equals active (case-insensitive).
 * @param {string|null|undefined} status
 */
export function isActiveStatus(status) {
    return String(status ?? '').trim().toLowerCase() === SubscriptionStatus.ACTIVE;
}
