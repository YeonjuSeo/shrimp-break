/**
 * 새우 브레이크 익명 사용 통계 수신기 (Google Apps Script).
 *
 * 앱이 보낸 이벤트를 이 스프레드시트의 탭 3개에 기록한다.
 *   install  : 처음 동의했을 때 한 줄
 *   settings : 타이머·자리 비움 설정을 바꿀 때마다 한 줄 (이전 값 → 새 값)
 *   daily    : 한 사람의 하루 요약 한 줄 (같은 날 다시 오면 그 줄을 덮어쓴다)
 *   consent  : 통계 보내기를 켜고 끈 기록 (처음 선택 + 메뉴에서 바꾼 것).
 *              "안 보내기"는 고유 ID 없이 한 번만 온다 → 누가 거절했는지는 알 수 없고 비율만 본다.
 * dashboard 탭은 setup() 을 한 번 실행하면 수식으로 만들어진다.
 *
 * 설정 방법은 telemetry/SETUP.md 참고.
 */

// 앱과 같은 값. 아무렇게나 들어오는 요청을 거르는 용도이지 비밀번호는 아니다.
const SHARED_KEY = 'shrimp-v1';
const MAX_EVENTS_PER_REQUEST = 50;

const INSTALL_COLUMNS = [
  'received_at', 'id', 'date', 'os', 'os_version', 'app_version', 'build', 'locale', 'monitors', 'max_scale',
  'separate_timers', 'interval_minutes', 'sitting_minutes', 'standing_minutes', 'away_threshold',
];

const CONSENT_COLUMNS = [
  'received_at', 'id', 'date', 'choice', 'source', 'os', 'os_version', 'app_version', 'build', 'locale',
];

const SETTINGS_COLUMNS = [
  'received_at', 'id', 'date', 'hour', 'os', 'app_version', 'build', 'field', 'from', 'to',
];

const HOUR_COLUMNS = Array.from({ length: 24 }, (_, h) => 'ack_h' + String(h).padStart(2, '0'));

const DAILY_COLUMNS = [
  'received_at', 'row_key', 'id', 'date', 'os', 'os_version', 'app_version', 'build', 'locale', 'monitors', 'max_scale',
  'separate_timers', 'interval_minutes', 'sitting_minutes', 'standing_minutes', 'away_threshold',
  'alerts', 'acknowledged', 'shrimp_clicks', 'tempura_clicks', 'avg_shrimps_at_click', 'max_shrimps',
  'avg_reaction_sec', 'max_reaction_sec', 'sat_minutes', 'stood_minutes', 'pauses', 'paused_minutes',
  'away_count', 'quick_restart', 'quick_sit', 'quick_stand', 'errors',
  ...HOUR_COLUMNS,
];

// 숫자처럼 보여도 글자로 남겨야 하는 열 (예: 26.6 이 숫자로 바뀌지 않도록)
const TEXT_COLUMNS = ['id', 'row_key', 'os_version', 'app_version', 'locale', 'from', 'to'];

function doPost(e) {
  let body;
  try {
    body = JSON.parse(e.postData.contents);
  } catch (error) {
    return reply('bad-json');
  }
  if (!body || body.key !== SHARED_KEY || !Array.isArray(body.events)) return reply('denied');

  const lock = LockService.getScriptLock();
  lock.waitLock(20000);
  try {
    const book = SpreadsheetApp.getActiveSpreadsheet();
    body.events.slice(0, MAX_EVENTS_PER_REQUEST).forEach(event => handleEvent(book, event));
  } finally {
    lock.releaseLock();
  }
  return reply('ok');
}

function handleEvent(book, event) {
  if (!event || typeof event.id !== 'string' || event.id.length < 8 || event.id.length > 64) return;
  const now = new Date();
  switch (event.type) {
    case 'consent':
      appendRow(sheetFor(book, 'consent', CONSENT_COLUMNS), CONSENT_COLUMNS, event, now, '');
      break;
    case 'install':
      appendRow(sheetFor(book, 'install', INSTALL_COLUMNS), INSTALL_COLUMNS, event, now, '');
      break;
    case 'settings':
      appendRow(sheetFor(book, 'settings', SETTINGS_COLUMNS), SETTINGS_COLUMNS, event, now, '');
      break;
    case 'daily':
      upsertRow(sheetFor(book, 'daily', DAILY_COLUMNS), DAILY_COLUMNS, event, now, event.id + '|' + event.date);
      break;
  }
}

function sheetFor(book, name, columns) {
  let sheet = book.getSheetByName(name);
  if (!sheet) {
    sheet = book.insertSheet(name);
    sheet.appendRow(columns);
    sheet.setFrozenRows(1);
    TEXT_COLUMNS.forEach(column => {
      const index = columns.indexOf(column);
      if (index >= 0) sheet.getRange(1, index + 1, sheet.getMaxRows(), 1).setNumberFormat('@');
    });
  }
  return sheet;
}

function rowValues(columns, event, now, rowKey) {
  return columns.map(column => {
    if (column === 'received_at') return now;
    if (column === 'row_key') return rowKey;
    if (column === 'date') return toDate(event.date);
    return clean(event[column]);
  });
}

/** 앱이 보낸 'yyyy-MM-dd'(사용자 PC 기준 날짜)를 시트 날짜로. 대시보드가 TODAY() 와 비교한다. */
function toDate(value) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(String(value || ''));
  return match ? new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3])) : '';
}

/** 숫자·참거짓은 그대로, 글자는 길이를 자르고 수식으로 해석되지 않게 한다. */
function clean(value) {
  if (value === undefined || value === null) return '';
  if (typeof value === 'number' || typeof value === 'boolean') return value;
  const text = String(value).slice(0, 100);
  return /^[=+\-@]/.test(text) ? "'" + text : text;
}

function appendRow(sheet, columns, event, now, rowKey) {
  sheet.appendRow(rowValues(columns, event, now, rowKey));
}

function upsertRow(sheet, columns, event, now, rowKey) {
  const values = [rowValues(columns, event, now, rowKey)];
  const keyColumn = columns.indexOf('row_key') + 1;
  const lastRow = sheet.getLastRow();
  const found = lastRow < 2 ? null : sheet.getRange(2, keyColumn, lastRow - 1, 1)
    .createTextFinder(rowKey).matchEntireCell(true).findNext();
  if (found) {
    sheet.getRange(found.getRow(), 1, 1, columns.length).setValues(values);
  } else {
    sheet.appendRow(values[0]);
  }
}

function reply(result) {
  return ContentService.createTextOutput(JSON.stringify({ result })).setMimeType(ContentService.MimeType.JSON);
}

// ── 처음 한 번만 실행: 탭과 대시보드 만들기 ────────────────────────────

function setup() {
  const book = SpreadsheetApp.getActiveSpreadsheet();
  sheetFor(book, 'install', INSTALL_COLUMNS);
  sheetFor(book, 'settings', SETTINGS_COLUMNS);
  sheetFor(book, 'daily', DAILY_COLUMNS);
  sheetFor(book, 'consent', CONSENT_COLUMNS);
  buildDashboard(book);
}

function buildDashboard(book) {
  const existing = book.getSheetByName('dashboard');
  if (existing) book.deleteSheet(existing);
  const sheet = book.insertSheet('dashboard', 0);

  const i = column => 'install!' + letter(INSTALL_COLUMNS, column) + ':' + letter(INSTALL_COLUMNS, column);
  const c = column => 'consent!' + letter(CONSENT_COLUMNS, column) + ':' + letter(CONSENT_COLUMNS, column);
  const d = column => 'daily!' + letter(DAILY_COLUMNS, column) + ':' + letter(DAILY_COLUMNS, column);
  const d2 = column => 'daily!' + letter(DAILY_COLUMNS, column) + '2:' + letter(DAILY_COLUMNS, column);
  const release = d('build') + ',"release"';
  const acked = d('acknowledged') + ',">0"';

  const rows = [
    ['🦐 새우 브레이크 통계 (테스트 빌드 제외)', ''],
    ['', ''],
    ['설치', ''],
    ['총 설치 수', `=COUNTIF(${i('build')},"release")`],
    ['Mac', `=COUNTIFS(${i('build')},"release",${i('os')},"mac")`],
    ['Windows', `=COUNTIFS(${i('build')},"release",${i('os')},"windows")`],
    ['처음 실행 때 통계 동의율', `=IFERROR(COUNTIFS(${c('build')},"release",${c('source')},"first_run",${c('choice')},"granted")/COUNTIFS(${c('build')},"release",${c('source')},"first_run"),0)`],
    ['메뉴에서 통계 끈 횟수', `=COUNTIFS(${c('build')},"release",${c('choice')},"turned_off")`],
    ['', ''],
    ['사용', ''],
    ['오늘 사용한 사람', `=COUNTIFS(${d('date')},TODAY(),${release})`],
    ['최근 7일 사용한 사람', `=IFERROR(COUNTUNIQUE(FILTER(${d2('id')},${d2('date')}>=TODAY()-6,${d2('build')}="release")),0)`],
    ['', ''],
    ['새우', ''],
    ['하루 평균 자세 바꾼 횟수', `=IFERROR(AVERAGEIFS(${d('acknowledged')},${release},${acked}),0)`],
    ['평균 몇 마리에서 일어났나', `=IFERROR(AVERAGEIFS(${d('avg_shrimps_at_click')},${release},${acked}),0)`],
    ['최고 기록 (최대로 쌓인 새우)', `=IFERROR(MAXIFS(${d('max_shrimps')},${release}),0)`],
    ['평균 버틴 시간 (초)', `=IFERROR(AVERAGEIFS(${d('avg_reaction_sec')},${release},${acked}),0)`],
    ['최장 버틴 시간 (초)', `=IFERROR(MAXIFS(${d('max_reaction_sec')},${release}),0)`],
    ['클릭한 새우', `=SUMIFS(${d('shrimp_clicks')},${release})`],
    ['클릭한 새우튀김', `=SUMIFS(${d('tempura_clicks')},${release})`],
    ['하루 평균 서 있던 시간 (분)', `=IFERROR(AVERAGEIFS(${d('stood_minutes')},${release},${acked}),0)`],
    ['하루 평균 앉아 있던 시간 (분)', `=IFERROR(AVERAGEIFS(${d('sat_minutes')},${release},${acked}),0)`],
    ['', ''],
    ['설정', ''],
    ['앉기·서기 따로 설정 쓰는 비율', `=IFERROR(COUNTIFS(${d('separate_timers')},TRUE,${release})/COUNTIFS(${release}),0)`],
    ['일시정지 사용 (총 횟수)', `=SUMIFS(${d('pauses')},${release})`],
    ['바로 시작·초기화 사용 (총 횟수)', `=SUMIFS(${d('quick_restart')},${release})+SUMIFS(${d('quick_sit')},${release})+SUMIFS(${d('quick_stand')},${release})`],
    ['자리 비움 발생 (총 횟수)', `=SUMIFS(${d('away_count')},${release})`],
    ['여러 모니터 사용 비율', `=IFERROR(COUNTIFS(${d('monitors')},">1",${release})/COUNTIFS(${release}),0)`],
    ['오류 (Windows, 총 횟수)', `=SUMIFS(${d('errors')},${release})`],
  ];
  sheet.getRange(1, 1, rows.length, 2).setValues(rows);

  // 인기 알림 간격 (사람-일 기준)
  const top = rows.length + 2;
  sheet.getRange(top, 1).setValue('인기 알림 간격 (타이머 한 개 모드, 사람-일 기준)');
  sheet.getRange(top + 1, 1).setFormula(
    `=IFERROR(QUERY({${d2('interval_minutes')},${d2('build')},${d2('separate_timers')}},` +
    `"select Col1, count(Col1) where Col2='release' and Col3=false group by Col1 order by count(Col1) desc limit 5 ` +
    `label Col1 '간격(분)', count(Col1) '횟수'",0),"아직 없음")`);
  sheet.getRange(top, 4).setValue('인기 앉기/서기 조합 (사람-일 기준)');
  sheet.getRange(top + 1, 4).setFormula(
    `=IFERROR(QUERY({${d2('sitting_minutes')},${d2('standing_minutes')},${d2('build')},${d2('separate_timers')}},` +
    `"select Col1, Col2, count(Col1) where Col3='release' and Col4=true group by Col1, Col2 order by count(Col1) desc limit 5 ` +
    `label Col1 '앉기(분)', Col2 '서기(분)', count(Col1) '횟수'",0),"아직 없음")`);

  // 시간대별 자세 바꾼 횟수
  const hoursRow = top + 9;
  sheet.getRange(hoursRow, 1).setValue('시간대별 자세 바꾼 횟수');
  HOUR_COLUMNS.forEach((column, hour) => {
    sheet.getRange(hoursRow + 1, hour + 1).setValue(hour + '시');
    sheet.getRange(hoursRow + 2, hour + 1).setFormula(`=SUMIFS(${d(column)},${release})`);
  });

  sheet.setColumnWidth(1, 280);
  sheet.getRange('A1').setFontWeight('bold').setFontSize(14);
  ['A3', 'A10', 'A14', 'A25'].forEach(cell => sheet.getRange(cell).setFontWeight('bold'));
  sheet.getRange(top, 1).setFontWeight('bold');
  sheet.getRange(top, 4).setFontWeight('bold');
  sheet.getRange(hoursRow, 1).setFontWeight('bold');
  ['B7', 'B26', 'B30'].forEach(cell => sheet.getRange(cell).setNumberFormat('0%'));
}

function letter(columns, name) {
  let index = columns.indexOf(name) + 1;
  if (index <= 0) throw new Error('unknown column ' + name);
  let result = '';
  while (index > 0) {
    const remainder = (index - 1) % 26;
    result = String.fromCharCode(65 + remainder) + result;
    index = Math.floor((index - 1) / 26);
  }
  return result;
}
