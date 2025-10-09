grammar Ex;
p : s EOF;
s : s '+' m | m ;
m : m '*' t | t ;
t : '1' | '2' | '3' | '4' ;
