'use strict';
// MOCK DATA: example systems for demonstrations. They are flagged with sample: true and shown
// with a "Sample" label in the UI. The hostnames do not exist.

module.exports = [
  { name: 'Finance Test Server', host: 'at-grz-srv01.example.invalid', environment: 'Test', os: 'Windows Server 2025', location: 'Graz', folder: 'Finance', tags: ['finance', 'sap'], favorite: true },
  { name: 'Finance Production', host: 'de-fra-srv12.example.invalid', environment: 'Production', os: 'Windows Server 2022', location: 'Frankfurt', folder: 'Finance', tags: ['finance'] },
  { name: 'Data Science Workstation', host: 'at-vie-ws07.example.invalid', environment: 'Development', os: 'Windows 11 Enterprise', location: 'Vienna', folder: 'Analytics', tags: ['gpu', 'python'], favorite: true },
  { name: 'Build Agent 03', host: 'nl-ams-build03.example.invalid', environment: 'Internal', os: 'Windows Server 2025', location: 'Amsterdam', folder: 'Engineering', tags: ['ci'] },
  { name: 'Legacy ERP Jump Host', host: 'de-muc-jmp01.example.invalid', environment: 'Production', os: 'Windows Server 2019', location: 'Munich', folder: 'Engineering', tags: ['jump host'] },
  { name: 'Training Lab VM', host: 'ch-zrh-lab02.example.invalid', environment: 'Test', os: 'Windows 11 Enterprise', location: 'Zurich', folder: 'Training', tags: ['lab'] },
].map((s) => ({ ...s, sample: true, description: 'Sample system (mock data). The host name does not exist.' }));
