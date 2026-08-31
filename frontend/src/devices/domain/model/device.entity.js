import { DeviceStatus } from "./device-status.enum.js";

export class Device {
  constructor(id=null, name="", type="", location="", projectId=null, status = "", macAddress="") {
    this.id = id;
    this.name = name;
    this.type = type; // canonical code (e.g., temperature, energy)
    this.location = location;
    this.projectId = projectId;
    this.status = status;
    this.macAddress = macAddress;
  }

  isOnline() {
    return this.status === DeviceStatus.ONLINE;
  }

  toggleStatus() {
    this.status = this.status === DeviceStatus.ONLINE ? DeviceStatus.OFFLINE : DeviceStatus.ONLINE;
  }
}
