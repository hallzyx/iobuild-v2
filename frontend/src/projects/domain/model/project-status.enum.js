/**
 * Project status labels — numeric enum from backend.
 */

export const ProjectStatus = Object.freeze({
    PLANNED: 0,
    ON_GOING: 1,
    ON_HOLD: 2,
    COMPLETED: 3,
    CANCELLED: 4,
});

export const PROJECT_STATUS_LABELS = Object.freeze({
    [ProjectStatus.PLANNED]: 'Planned',
    [ProjectStatus.ON_GOING]: 'On going',
    [ProjectStatus.ON_HOLD]: 'On hold',
    [ProjectStatus.COMPLETED]: 'Completed',
    [ProjectStatus.CANCELLED]: 'Cancelled',
});
