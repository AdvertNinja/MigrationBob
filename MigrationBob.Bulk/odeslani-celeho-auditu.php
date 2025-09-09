<?php
session_start();
header('Content-Type: application/json');

$raw = file_get_contents('php://input');
$ct  = $_SERVER['CONTENT_TYPE'] ?? '';
if (stripos($ct, 'application/json') !== false) {
  $in = json_decode($raw, true);
} else {
  $in = $_POST;
}
if (!is_array($in)) $in = [];

$country   = trim((string)($in['country'] ?? ''));
$reportUrl = trim((string)($in['reportUrl'] ?? ''));
$total     = (int)($in['total'] ?? 0);
$okCount   = (int)($in['ok'] ?? 0);
$nokCount  = (int)($in['nok'] ?? 0);
$pages     = is_array($in['pages'] ?? null) ? $in['pages'] : [];

if ($country === '' || $total <= 0) {
  http_response_code(400);
  echo json_encode(['status'=>'error','message'=>'Invalid data']);
  exit;
}

function extractEmail($v) {
  if (!is_string($v)) return null;
  $v = trim($v);
  if (preg_match('/<([^>]+)>/', $v, $m)) $v = $m[1];
  $v = preg_replace('/^mailto:/i', '', $v);
  $v = filter_var($v, FILTER_SANITIZE_EMAIL);
  return filter_var($v, FILTER_VALIDATE_EMAIL) ?: null;
}

$rawAuth   = $_SESSION['auth_user'] ?? '';
$authEmail = extractEmail($rawAuth);
$to        = $authEmail ?: 'theadvertninja@gmail.com';
$from      = 'cemex@advert.ninja';
$subject   = 'Souhrn auditu — '.$country;

$percentOk = $total > 0 ? round(($okCount / $total) * 100, 1) : 0.0;
$isReady   = ($percentOk === 100.0);

$h  = "<div style='background:#fff;border:1px solid #000;border-radius:10px;padding:20px;font-family:Arial,sans-serif;color:#111'>";
$h .= "<h2 style='margin:0 0 12px'>Souhrn auditu — ".htmlspecialchars($country,ENT_QUOTES,'UTF-8')."</h2>";
$h .= "<div style='font-size:32px;line-height:1.2;font-weight:700;margin:0 0 8px'>Audit byl úspěšný na {$percentOk}%</div>";
$h .= $isReady
  ? "<div style='font-size:16px;margin:0 0 16px'>Gratulujeme! Stránky jsou připraveny pro spuštění!</div>"
  : "<div style='font-size:16px;margin:0 0 16px'>Stránky nejsou připraveny pro spuštění.</div>";
$h .= "<p><strong>Výsledky:</strong> ".(int)$okCount." OK • ".(int)$nokCount." NOK • ".(int)$total." celkem</p>";

if ($reportUrl !== '') {
  $h .= "<p><a href='".htmlspecialchars($reportUrl,ENT_QUOTES,'UTF-8')."'>Stažení JSON reportu</a></p>";
} else {
  $h .= "<p style='color:#666'><em>Report URL není k dispozici — audit byl ukončen bez „done“ události, posílám pouze souhrn.</em></p>";
}

if (!empty($pages)) {
  $h .= "<hr style='border:none;border-top:1px solid #eee;margin:16px 0'>";
  $h .= "<h3 style='margin:0 0 10px'>Detaily stránek</h3>";
  foreach ($pages as $p) {
    $url = htmlspecialchars((string)($p['url'] ?? ''), ENT_QUOTES, 'UTF-8');
    $h  .= "<div style='margin:10px 0 14px'>";
    $h  .= "<div style='font-weight:600;margin-bottom:6px'>".$url."</div>";
    $checks = is_array($p['checks'] ?? null) ? $p['checks'] : [];
    if (!empty($checks)) {
      $h .= "<ul style='margin:6px 0;padding-left:0;list-style:none'>";
      foreach ($checks as $c) {
        $isOk = !empty($c['ok']);
        $name = htmlspecialchars((string)($c['name'] ?? ''), ENT_QUOTES, 'UTF-8');
        $det  = htmlspecialchars((string)($c['details'] ?? ''), ENT_QUOTES, 'UTF-8');
        $bg   = $isOk ? "#eaffea" : "#ffecec";
        $col  = $isOk ? "#0a0"    : "#a00";
        $tag  = $isOk ? "OK"      : "NOK";
        $h   .= "<li style='background:{$bg};color:{$col};padding:6px 8px;border-radius:6px;margin:6px 0;border:1px solid #ddd'><span style='font-weight:700'>{$tag}</span> — <strong>{$name}</strong>".($det ? " — {$det}" : "")."</li>";
      }
      $h .= "</ul>";
    }
    $h .= "</div>";
  }
}

$h .= "<p style='color:#777;font-size:12px;margin-top:8px'>Odesláno: ".date('Y-m-d H:i:s')."</p>";
$h .= "</div>";

$headers  = "MIME-Version: 1.0\r\n";
$headers .= "Content-Type: text/html; charset=UTF-8\r\n";
$headers .= "From: MigrationBob <{$from}>\r\n";
$headers .= "Reply-To: {$from}\r\n";
$okSend   = @mail($to, $subject, $h, $headers, "-f{$from}");

echo json_encode($okSend ? ['status'=>'success'] : ['status'=>'error','message'=>'mail() failed']);
