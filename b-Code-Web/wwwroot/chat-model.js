// Raster dimensions are checked before decoding, so oversized attachments never enter the renderer.
const maxBytes=8*1024*1024,maxPixels=40_000_000;
export function imageBytes(encoded) {
  if(typeof encoded!=='string'||!encoded.length||encoded.length>11184812)throw new Error('图片为空或超过 8 MiB');
  let binary;try {binary=atob(encoded);}catch{throw new Error('图片不是有效 base64');}
  if(binary.length>maxBytes)throw new Error('图片超过 8 MiB');
  const bytes=Uint8Array.from(binary,c=>c.charCodeAt(0));
  const info=imageInfo(bytes);
  if(!info.width||!info.height||info.width*info.height>maxPixels)throw new Error('图片尺寸无效或超过 4000 万像素');
  return {bytes,...info};
}
export function imageInfo(bytes) {
  const view=new DataView(bytes.buffer,bytes.byteOffset,bytes.byteLength),ascii=(offset,size)=>String.fromCharCode(...bytes.slice(offset,offset+size));
  if(bytes.length>=24&&ascii(1,3)==='PNG'&&bytes[0]===137)return {type:'image/png',width:view.getUint32(16),height:view.getUint32(20)};
  if(bytes.length>=10&&/^GIF8[79]a$/.test(ascii(0,6)))return {type:'image/gif',width:view.getUint16(6,true),height:view.getUint16(8,true)};
  if(bytes.length>=30&&ascii(0,4)==='RIFF'&&ascii(8,4)==='WEBP') {
    const chunk=ascii(12,4);
    const uint24=o=>bytes[o]+(bytes[o+1]<<8)+(bytes[o+2]<<16);
    if(chunk==='VP8X')return {type:'image/webp',width:uint24(24)+1,height:uint24(27)+1};
    if(chunk==='VP8 '&&bytes[23]===157&&bytes[24]===1&&bytes[25]===42)return {type:'image/webp',width:view.getUint16(26,true)&0x3fff,height:view.getUint16(28,true)&0x3fff};
    if(chunk==='VP8L'&&bytes[20]===47)return {type:'image/webp',width:1+(bytes[21]|(bytes[22]&63)<<8),height:1+((bytes[22]>>6)|bytes[23]<<2|(bytes[24]&15)<<10)};
  }
  if(bytes.length>=4&&bytes[0]===255&&bytes[1]===216) {
    let offset=2;
    while(offset+4<=bytes.length) {
      if(bytes[offset]!==255)break;
      const marker=bytes[offset+1],length=view.getUint16(offset+2);
      if(length<2||offset+2+length>bytes.length)break;
      if([192,193,194,195,197,198,199,201,202,203,205,206,207].includes(marker)&&length>=7)
        return {type:'image/jpeg',width:view.getUint16(offset+7),height:view.getUint16(offset+5)};
      offset+=2+length;
    }
  }
  throw new Error('图片编码不支持或文件不完整');
}
export function attachments(row) {
  let images=row.images;
  if(typeof images==='string'){try{images=JSON.parse(images);}catch{throw new Error('图片附件列表不是有效 JSON');}}
  if(images==null)return [];
  if(!Array.isArray(images))throw new Error('图片附件列表格式无效');
  return images.slice(0,16).map(image=>({name:String(image?.name??'图片'),path:String(image?.path??'')}));
}
export async function rasterBlob(info,maxSide=360) {
  const blob=new Blob([info.bytes],{type:info.type});
  const scale=Math.min(1,maxSide/Math.max(info.width,info.height)),width=Math.max(1,Math.round(info.width*scale)),height=Math.max(1,Math.round(info.height*scale));
  const bitmap=await createImageBitmap(blob,{resizeWidth:width,resizeHeight:height});
  try {
    const canvas=document.createElement('canvas');canvas.width=width;canvas.height=height;
    canvas.getContext('2d').drawImage(bitmap,0,0,width,height);
    return await new Promise((resolve,reject)=>canvas.toBlob(result=>result?resolve(result):reject(new Error('图片解码失败')),'image/png'));
  }finally{bitmap.close();}
}
