export class GroupNameError extends RangeError {
  constructor(code, message) {
    super(message);
    this.name = 'GroupNameError';
    this.code = code;
  }
}

export function validateGroupName(name, groups, except = null) {
  const trimmed = typeof name === 'string' ? name.trim() : '';
  if (!trimmed || [...trimmed].length > 40) {
    throw new GroupNameError('length', 'Group name must be 1 to 40 characters.');
  }
  if (groups.some((group) => group.id !== except && group.name.toLowerCase() === trimmed.toLowerCase())) {
    throw new GroupNameError('duplicate', 'A group with that name already exists.');
  }
  return trimmed;
}

// Keep SessionOrder as the only owner of persistence and membership.
export class Groups {
  constructor(order) { this.order = order; }

  validateName(name, except = null) {
    return validateGroupName(name, this.order.groups, except);
  }

  // Pass the current unpinned live rows, as for SessionOrder.groupRows.
  counts(entries) {
    return new Map(this.order.groupRows(entries).groups.map(({ id, count }) => [id, count]));
  }
}
