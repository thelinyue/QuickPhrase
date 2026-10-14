import assert from 'node:assert/strict';
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';

// 在独立临时仓库执行真实 PowerShell 校验，不修改产品仓库标签或用户数据。
const fixtureRoot = path.resolve('.build/release-script-tests');
function fixture(t) {
  mkdirSync(fixtureRoot, { recursive: true });
  const dir = mkdtempSync(path.join(fixtureRoot, 'case-'));
  assert.equal(path.dirname(dir), fixtureRoot);
  t.after(() => rmSync(dir, { recursive: true, force: true }));
  mkdirSync(path.join(dir, 'scripts'));
  mkdirSync(path.join(dir, 'src-tauri'));
  for (const script of ['check-release.ps1', 'package-release.ps1']) copyFileSync(path.join('scripts', script), path.join(dir, 'scripts', script));
  writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ version: '0.1.1' }));
  writeFileSync(path.join(dir, 'src-tauri/tauri.conf.json'), JSON.stringify({ version: '0.1.1' }));
  writeFileSync(path.join(dir, 'src-tauri/Cargo.toml'), '[package]\nname = "quickphrase"\nversion = "0.1.1"\n[dependencies]\n');
  return dir;
}
function run(dir, args, script = 'check-release.ps1') {
  const result = spawnSync(process.platform === 'win32' ? 'powershell.exe' : 'pwsh', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', path.join(dir, 'scripts', script), ...args], { cwd: dir, encoding: 'utf8', timeout: 20000 });
  assert.ifError(result.error);
  return result;
}
function accepted(dir, args) { const result = run(dir, args); assert.equal(result.status, 0, result.stderr); }
function rejected(dir, args, reason, script) {
  const result = run(dir, args, script);
  assert.notEqual(result.status, 0, '无效请求意外通过');
  assert.ok(result.stderr.includes(reason), result.stderr);
}
function git(dir, args) {
  const result = spawnSync('git', ['-C', dir, '-c', 'user.name=Release Script Test', '-c', 'user.email=release-test@example.invalid', ...args], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
}
const candidate = ['-Version', '0.1.1-rc.1', '-Channel', 'prerelease', '-GitRef', 'refs/heads/master'];
const stable = ['-Version', '0.1.1', '-Channel', 'stable', '-GitRef', 'refs/tags/v0.1.1'];

test('本地构建允许默认版本和同基线 RC', t => {
  const dir = fixture(t); accepted(dir, []); accepted(dir, ['-Version', '0.1.1-rc.2']);
});
test('拒绝跨版本、无效 RC 和路径输入', t => {
  const dir = fixture(t);
  for (const version of ['0.1.2', '0.1.1-rc.0', '0.1.1-rc.01', '../outside']) rejected(dir, ['-Version', version], '版本必须是');
});
test('拒绝 Tauri 源码版本不一致', t => {
  const dir = fixture(t); writeFileSync(path.join(dir, 'src-tauri/tauri.conf.json'), '{"version":"0.1.2"}');
  rejected(dir, [], '稳定版本必须一致');
});
test('拒绝 Cargo 源码版本不一致', t => {
  const dir = fixture(t); writeFileSync(path.join(dir, 'src-tauri/Cargo.toml'), '[package]\nversion = "0.1.2"\n');
  rejected(dir, [], '稳定版本必须一致');
});
test('手动流程不能省略版本', t => {
  rejected(fixture(t), ['-Channel', 'stable'], '手动发布必须提供版本');
});
test('候选版本和来源正确即可通过，无需人工确认参数', t => { accepted(fixture(t), candidate); });
test('候选拒绝非 master 来源', t => {
  rejected(fixture(t), [...candidate.slice(0, 4), '-GitRef', 'refs/heads/other'], '候选构建只能从 master');
});
test('候选拒绝稳定版本', t => {
  rejected(fixture(t), ['-Version', '0.1.1', '-Channel', 'prerelease', '-GitRef', 'refs/heads/master'], '候选构建必须使用 rc.N');
});
test('正式版拒绝错误标签名称', t => {
  rejected(fixture(t), [...stable.slice(0, 4), '-GitRef', 'refs/tags/v0.1.2'], '正式发布必须从 refs/tags/v0.1.1');
});
test('正式版无需人工确认参数，仍要求标签存在且指向当前提交', t => {
  const dir = fixture(t); git(dir, ['init', '--quiet', '-b', 'master']); git(dir, ['add', '.']); git(dir, ['commit', '--quiet', '-m', 'fixture']);
  const args = stable;
  rejected(dir, args, '正式标签不存在或没有指向当前源码提交');
  git(dir, ['tag', 'v0.1.1']); accepted(dir, args);
  git(dir, ['commit', '--quiet', '--allow-empty', '-m', 'new source']);
  rejected(dir, args, '正式标签不存在或没有指向当前源码提交');
});
test('打包拒绝缺失产物', t => { rejected(fixture(t), [], '缺少构建产物', 'package-release.ps1'); });
test('打包拒绝错误程序版本', t => {
  const dir = fixture(t); mkdirSync(path.join(dir, 'src-tauri/target/release'), { recursive: true });
  writeFileSync(path.join(dir, 'src-tauri/target/release/QuickPhrase.exe'), 'not an executable');
  rejected(dir, [], '构建产物版本不一致', 'package-release.ps1');
});
