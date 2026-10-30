"""스크린샷에서 영역을 잘라 템플릿으로 저장.
  python make_template.py <png> <x> <y> <w> <h> <이름> [--switch]
"""
import sys, os, cv2
import vision as V

def main():
    a = sys.argv[1:]
    if len(a) < 6:
        print(__doc__); return
    img = V.imread(a[0]); x, y, w, h = map(int, a[1:5]); name = a[5]
    sub = "switch" if "--switch" in a else "phone"
    out = os.path.join(V.TPL_DIR, sub, name + ".png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    V.imwrite(out, img[y:y+h, x:x+w])
    print("저장:", out, img[y:y+h, x:x+w].shape)

if __name__ == "__main__":
    main()
