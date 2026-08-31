import { Device } from '../domain/model/device.entity.js';
import { DeviceStatus } from '../domain/model/device-status.enum.js';

export class DeviceAssembler {
  static toEntity(deviceData) {
    const statuses = [DeviceStatus.ONLINE, DeviceStatus.OFFLINE];
    const status = deviceData.status && (deviceData.status === DeviceStatus.ONLINE || deviceData.status === DeviceStatus.OFFLINE)
      ? deviceData.status
      : statuses[Math.floor(Math.random() * statuses.length)];

    return new Device(
      deviceData.id,
      deviceData.name,
      deviceData.type,
      deviceData.location,
      deviceData.projectId,
      status,
      deviceData.macAddress || ""
    );
  }

  static toEntityList(devicesData) {
    return devicesData.map(deviceData => this.toEntity(deviceData));
  }

  static toResource(device) {
    return {
      id: device.id,
      name: device.name,
      type: device.type,
      location: device.location,
      projectId: device.projectId,
      status: device.status,
      macAddress: device.macAddress
    };
  }
}
