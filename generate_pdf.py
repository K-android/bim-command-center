import subprocess
import os
import sys

edge_path = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
html_path = os.path.abspath("architecture_specification.html")
pdf_path = os.path.abspath("BIM_Command_Center_Architecture_Specification.pdf")

url = "file:///" + html_path.replace("\\", "/")

cmd = [
    edge_path,
    "--headless=new",
    "--disable-gpu",
    "--no-pdf-header-footer",
    f"--print-to-pdf={pdf_path}",
    url
]

print("Running command:", " ".join(cmd))
proc = subprocess.run(cmd, capture_output=True, text=True)
print("Return code:", proc.returncode)
print("Stdout:", proc.stdout)
print("Stderr:", proc.stderr)

if os.path.exists(pdf_path):
    print(f"SUCCESS: PDF generated successfully! Size: {os.path.getsize(pdf_path)} bytes")
else:
    print("FAILED: PDF was not created.")
