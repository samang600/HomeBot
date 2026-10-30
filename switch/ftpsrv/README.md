# ftpsrv (세이브 쓰기 수정판)

HomeBot은 게임을 켜지 않고 스위치 게임 세이브 파일을 FTP로 직접 고칩니다.
원본 [ITotalJustice/ftpsrv](https://github.com/ITotalJustice/ftpsrv)는 `save_writable=1`이어도
`/save:/<사용자>/files` 아래 세이브 파일 쓰기(STOR)가 항상 실패해서(EROFS) 한 줄을 고친 판을 씁니다.

- 수정 내용: `0001-Nx-allow-writing-save-files-under-save-user-files-wh.patch`
  (`src/platform/nx/vfs/vfs_nx_save.c`의 open() 쓰기 허용 조건)
- 빌드: 원본 저장소를 받아 이 패치를 적용(`git am`)한 뒤 원본 README의 스위치 빌드 방법대로 (devkitPro)
- 릴리스의 `ftpsrv_savefix.zip`은 이 패치로 빌드한 것입니다. SD 카드 루트에 풀고
  `config/ftpsrv/config.ini.template` → `config.ini`로 이름을 바꾼 뒤 `anon = 1`, `[Nx]` 아래
  `mount_devices = 1`, `save_writable = 1`로 두고 재부팅하세요.

ftpsrv의 저작권과 라이선스는 원본 저장소를 따릅니다.
