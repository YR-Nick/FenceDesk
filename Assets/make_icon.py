from PIL import Image, ImageDraw, ImageFilter
import math, os
S=1024
img=Image.new('RGBA',(S,S),(0,0,0,0))
# shadow layer
sh=Image.new('RGBA',(S,S),(0,0,0,0)); d=ImageDraw.Draw(sh)
d.rounded_rectangle((70,90,954,974), radius=220, fill=(0,0,0,170))
sh=sh.filter(ImageFilter.GaussianBlur(34)); img.alpha_composite(sh)
# base rounded square gradient
base=Image.new('RGBA',(S,S),(0,0,0,0)); bd=ImageDraw.Draw(base)
for y in range(70,970):
    t=(y-70)/900
    r=int(30*(1-t)+10*t); g=int(36*(1-t)+12*t); b=int(52*(1-t)+18*t)
    bd.line((70,y,954,y), fill=(r,g,b,255))
mask=Image.new('L',(S,S),0); ImageDraw.Draw(mask).rounded_rectangle((70,70,954,954), radius=220, fill=255)
img.paste(base,(0,0),mask)
# inner border
bd=ImageDraw.Draw(img)
bd.rounded_rectangle((70,70,954,954), radius=220, outline=(255,255,255,38), width=5)
# top-left glow
glow=Image.new('RGBA',(S,S),(0,0,0,0)); gd=ImageDraw.Draw(glow)
gd.ellipse((40,10,700,520), fill=(51,214,192,55)); glow=glow.filter(ImageFilter.GaussianBlur(120)); img.alpha_composite(Image.composite(glow,Image.new('RGBA',(S,S),(0,0,0,0)),mask))
# fence panels
panels=[(140,190,884,356,(51,214,192)),(140,408,884,574,(110,168,255)),(140,626,884,792,(182,156,255))]
for (x0,y0,x1,y1,col) in panels:
    layer=Image.new('RGBA',(S,S),(0,0,0,0)); ld=ImageDraw.Draw(layer)
    ld.rounded_rectangle((x0,y0,x1,y1), radius=48, fill=(255,255,255,23))
    ld.rounded_rectangle((x0,y0,x1,y1), radius=48, outline=(255,255,255,30), width=3)
    # accent bar
    ld.rounded_rectangle((x0+4,y0+36,x0+20,y1-36), radius=8, fill=col+(255,))
    # dots
    for i in range(4):
        cx=x0+90+i*82; cy=(y0+y1)//2
        ld.rounded_rectangle((cx-23,cy-23,cx+23,cy+23), radius=14, fill=(255,255,255,55))
    img.alpha_composite(layer)
# bottom accent line
bd.rounded_rectangle((360,860,664,880), radius=10, fill=(51,214,192,180))
img=img.resize((512,512), Image.LANCZOS)
img.save(r'D:\项目\FenceDesk\Assets\logo.png')
sizes=[16,20,24,32,40,48,64,128,256]
img.save(r'D:\项目\FenceDesk\Assets\FenceDesk.ico', format='ICO', sizes=[(s,s) for s in sizes])
print('saved', os.path.getsize(r'D:\项目\FenceDesk\Assets\FenceDesk.ico'))
